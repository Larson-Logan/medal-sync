using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Integration;

/// <summary>Real rclone transfers with an isolated metadata service, or explicit opt-in to a disposable Drive collection.</summary>
[SupportedOSPlatform("windows")]
internal static class CloudHandoff
{
    internal static void Run(string rclone, Action<bool, string> check, string? liveRemote = null, string? liveCredentials = null, bool testMount = false)
    {
        using var fixture = new MedalFixture();
        string credentials = liveCredentials ?? fixture.PathFor("rclone.conf");
        string remote;
        string journalId;
        using var handler = new MetadataService();
        using var client = new HttpClient(handler);
        if (liveRemote is null)
        {
            Directory.CreateDirectory(fixture.PathFor("drive"));
            File.WriteAllText(credentials, "[gdrive]\ntype = alias\nremote = " + fixture.PathFor("drive") + "\n");
            remote = "gdrive:CloudHandoff";
            journalId = "synthetic-journal-id";
        }
        else
        {
            if (!ReplicationConfig.ValidRemote(liveRemote) || liveCredentials is null || !File.Exists(liveCredentials))
                throw new InvalidDataException("Live Drive acceptance needs an explicit named remote with a dedicated folder and an existing local rclone config.");
            remote = liveRemote.TrimEnd('/') + "/medal-sync-acceptance-" + Guid.NewGuid().ToString("N");
            journalId = GoogleDriveJournal.GenerateId(rclone, remote, credentials);
            Console.WriteLine("Live Google Drive acceptance uses only the new synthetic collection at " + remote + ". It is retained for review.");
        }
        char letter = testMount ? Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Reverse().First(c =>
            !Directory.Exists(c + @":\") && !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase))) : 'M';
        var config = new MedalSyncConfig(1, Guid.NewGuid().ToString("N"), MedalFixture.AccountId,
            fixture.PathFor("first-journal"), remote, letter + @":\Clips", fixture.PathFor("cache"), rclone,
            new(1, 1, 1, 60, 1, 1, 2, FreePort()), .001, [])
        {
            StorageMode = StorageMode.Cloud, CloudJournalId = journalId,
            Replication = new(fixture.PathFor("first-media"), credentials, []),
        };
        config.Validate();
        var runner = new RcloneRunner(rclone);
        runner.Run(["mkdir", remote, "--config", credentials], TimeSpan.FromMinutes(2));
        bool interrupt = false;
        string Execute(IReadOnlyList<string> args)
        {
            if (liveRemote is null && args[0] == "lsjson" && args.Contains("--stat")) return """{"ID":"synthetic-drive-folder","IsDir":true}""";
            if (interrupt && args[0] == "copy" && args[2].Contains("/.medal-sync/", StringComparison.Ordinal))
                throw new OperationCanceledException("Synthetic interruption before journal commit");
            return runner.Run(args, TimeSpan.FromMinutes(5), captureOutput: true);
        }
        GoogleDriveJournal Api(MedalSyncConfig settings) => liveRemote is null
            ? new(journalId, () => "synthetic-token", client) : GoogleDriveJournal.For(settings);
        var first = new DeviceIdentity(new string('a', 32), "Synthetic first PC");
        var second = new DeviceIdentity(new string('b', 32), "Synthetic second PC");
        var versions = new MedalVersions("2643.583.1", "2643.3040.1");
        var state = new LocalState(fixture.PathFor("first-state"));
        new RcloneAssetChecker(config, state).Initialize(false);
        StorageIdentity.Initialize(config.MountedCapture.Display, config.LibraryId);
        const string clip = "#clip [test] Ω.mp4";
        File.WriteAllText(config.MountedCapture.Append(clip).Display, "Synthetic media; no real recordings.");
        string db = fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "clip", video: config.Capture.Append(clip).Display);
        var snapshot = LibraryDatabase.ReadSnapshot(db, config.AccountId);
        new StorageReplication(config, state, () => { }).Publish(snapshot);
        var backend = new CloudCoordination(config, Api(config), Execute);
        string session = new('1', 32);
        backend.Create(snapshot, new(1, [], []), first, session, versions, DateTimeOffset.UtcNow);
        var store = CoordinationStore.OpenCloud(config, backend);
        store.Update(j => Ownership.Acquire(j, first, session, DateTimeOffset.UtcNow), TimeSpan.Zero);
        store.Update(j => Ownership.MarkActive(j, first, session, DateTimeOffset.UtcNow), TimeSpan.Zero);
        File.WriteAllText(config.MountedCapture.Append("new.mp4").Display, "Synthetic new recording from PC one.");
        MedalFixture.AddContent(db, "new", video: config.Capture.Append("new.mp4").Display);
        var changed = LibraryDatabase.ReadSnapshot(db, config.AccountId);
        store.Update(j => Ownership.BeginDrain(j, first, session, DateTimeOffset.UtcNow), TimeSpan.Zero);
        new StorageReplication(config, state, () => { }).Publish(changed);
        store.Update(j => Ownership.BeginPublish(j, first, session, DateTimeOffset.UtcNow), TimeSpan.Zero);
        store.Generations.Publish(1, 0, session, first, changed, new(1, [], []), versions, DateTimeOffset.UtcNow);
        interrupt = true;
        bool interrupted = false;
        try { store.Update(j => Ownership.CompletePublish(j, first, session, 1, DateTimeOffset.UtcNow), TimeSpan.Zero); }
        catch (OperationCanceledException) { interrupted = true; }
        check(interrupted && store.ReadJournal() is { State: OwnershipState.Publishing, CommittedGeneration: 0 },
            "cloud interruption keeps ownership and the previous committed generation");
        interrupt = false;
        store.Update(j => Ownership.CompletePublish(j, first, session, 1, DateTimeOffset.UtcNow), TimeSpan.Zero);

        var other = config with { CoordinationRoot = fixture.PathFor("second-journal"),
            Replication = config.Replication! with { LocalCopyRoot = fixture.PathFor("second-media") } };
        var otherStore = CoordinationStore.OpenCloud(other, new(other, Api(other), Execute));
        var generation = otherStore.Generations.Read(otherStore.ReadJournal().CommittedGeneration);
        check(generation.Snapshot.Digest() == changed.Digest() && otherStore.Generations.Chain(1).Count() == 2,
            "another PC downloads real rclone generation files and verifies the shared library history");
        new StorageReplication(other, new(fixture.PathFor("second-state")), () => { }).Prepare(generation.Snapshot);
        check(File.ReadAllBytes(other.MountedCapture.Append(clip).Display).SequenceEqual(File.ReadAllBytes(config.MountedCapture.Append(clip).Display))
            && File.ReadAllBytes(other.MountedCapture.Append("new.mp4").Display).SequenceEqual(File.ReadAllBytes(config.MountedCapture.Append("new.mp4").Display)),
            "the second PC hydrates and byte-verifies both Google Drive media paths, including Unicode and filter characters");
        string secondSession = new('2', 32);
        otherStore.Update(j => Ownership.Acquire(j, second, secondSession, DateTimeOffset.UtcNow), TimeSpan.Zero);
        check(store.ReadJournal().Owner == second, "the first PC observes the second PC's authoritative cloud ownership");
        otherStore.Update(j => Ownership.AbortStart(j, second, secondSession, "synthetic acceptance complete", DateTimeOffset.UtcNow), TimeSpan.Zero);
        var a = Api(config); var b = Api(other);
        var oldA = a.Read(); var oldB = b.Read();
        var request = new HandoffRequest(1, first, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        check(a.CompareExchange(oldA, oldA.Value with { Requests = [request] })
            && !b.CompareExchange(oldB, oldB.Value with { Requests = [] }), "the metadata endpoint rejects a stale ETag after a successful conditional write");
        store.RemoveRequest(first);
        check(store.ReadJournal().State == OwnershipState.Clean && File.Exists(config.MountedCapture.Append(clip).Display),
            "cloud handoff ends Ready and preserves the first PC's persistent copy");
        if (testMount) MountRoundTrip(fixture, other, otherStore, store, first: config, check);
    }

    private sealed class Medal : IMedalProcess
    {
        public bool Running;
        public bool IsRunning() => Running;
        public void Launch() => Running = true;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void MountRoundTrip(MedalFixture fixture, MedalSyncConfig config, CoordinationStore store, CoordinationStore firstStore,
        MedalSyncConfig first, Action<bool, string> check)
    {
        var state = new LocalState(fixture.PathFor("mounted-state"));
        var clock = new SystemClock();
        var mount = new RcloneClipMount(config, state, .01, clock);
        try
        {
            string db = fixture.CreateLibrary("mounted-pc");
            var medal = new Medal();
            var checker = new RcloneAssetChecker(config, state);
            var coordinator = new SessionCoordinator(store, new SqliteLocalLibrary(db, config.AccountId, state.BackupDirectory, clock, config.CaptureFolder),
                mount, medal, checker, clock, new(new(new string('c', 32), "Synthetic mounted PC"), config.Capture,
                    new("2643.583.1", "2643.3040.1"), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250)),
                replication: new StorageReplication(config, state, () => { if (medal.Running) throw new IOException("Synthetic Medal reopened."); }));
            var started = coordinator.Launch();
            check(started.Status == LaunchStatus.Started && File.ReadAllText(config.Capture.Append("new.mp4").Display) == "Synthetic new recording from PC one.",
                "cloud mode restores a database and reads hydrated media through a real WinFsp drive without SMB: " + started.Status + ": " + started.Message);
            File.WriteAllText(config.Capture.Append("mounted-new.mp4").Display, "Synthetic recording through the cloud-mode clip drive.");
            MedalFixture.AddContent(db, "mounted-new", video: config.Capture.Append("mounted-new.mp4").Display);
            medal.Running = false;
            var finished = coordinator.Finish(started.Session!);
            check(finished.Status == FinishStatus.Published && !mount.IsRunning && store.ReadJournal() is { State: OwnershipState.Clean, CommittedGeneration: 2 },
                "cloud mode drains the real mount, verifies rclone media and snapshot uploads, and only then releases ownership: " + finished.Message);
            var snapshot = firstStore.Generations.Read(firstStore.ReadJournal().CommittedGeneration).Snapshot;
            new StorageReplication(first, new(fixture.PathFor("return-state")), () => { }).Prepare(snapshot);
            check(File.ReadAllText(first.MountedCapture.Append("mounted-new.mp4").Display) == "Synthetic recording through the cloud-mode clip drive.",
                "the previous PC hydrates the recording and library generation published by the real cloud-mode mount");
        }
        finally { if (mount.IsRunning) mount.Stop(); }
    }

    private sealed class MetadataService : HttpMessageHandler
    {
        private string? description;
        private string? id;
        private int version;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Post)
            {
                if (description is not null) return Reply(HttpStatusCode.Conflict);
                using var create = JsonDocument.Parse(body);
                id = create.RootElement.GetProperty("id").GetString();
                description = create.RootElement.GetProperty("description").GetString();
                version++;
                return Reply(HttpStatusCode.OK);
            }
            if (description is null) return Reply(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get) return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new
            { id, description, etag = "\"" + version + "\"", mimeType = "application/json", labels = new { trashed = false } }));
            if (request.Headers.IfMatch.Single().Tag != "\"" + version + "\"") return Reply(HttpStatusCode.PreconditionFailed);
            using var patch = JsonDocument.Parse(body);
            description = patch.RootElement.GetProperty("description").GetString();
            version++;
            return Reply(HttpStatusCode.OK);
        }
        private static HttpResponseMessage Reply(HttpStatusCode code, string body = "{}") => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

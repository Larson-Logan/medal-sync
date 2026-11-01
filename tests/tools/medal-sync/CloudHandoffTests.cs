using System.Net;
using System.Text;
using System.Text.Json;
using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class CloudHandoffTests : IDisposable
{
    private readonly MedalFixture fixture = new();
    private const string LibraryId = "0123456789abcdef0123456789abcdef";
    private const string FileId = "synthetic-journal-id";
    private static readonly DeviceIdentity First = new(new string('a', 32), "First PC");
    private static readonly DeviceIdentity Second = new(new string('b', 32), "Second PC");
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    public void Dispose() => fixture.Dispose();

    private MedalSyncConfig Config(string pc) => new(1, LibraryId, MedalFixture.AccountId, fixture.PathFor(pc, "journal"),
        "gdrive:MedalSync", @"M:\Clips", fixture.PathFor(pc, "cache"), @"C:\Tools\rclone.exe", new(5, 20, 1, 300, 30, 1000, 2, MountConfig.DefaultControlPort), 4, [])
    {
        StorageMode = StorageMode.Cloud, CloudJournalId = FileId,
        Replication = new(fixture.PathFor(pc, "media"), fixture.PathFor(pc, "private", "rclone.conf"), []),
    };

    private LibrarySnapshot Snapshot(string name, params string[] clips)
    {
        string db = fixture.CreateLibrary(name);
        foreach (string clip in clips) MedalFixture.AddContent(db, clip, video: @"M:\Clips\" + clip + ".mp4");
        return LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
    }

    // This models the API's atomic metadata/ETag operation, not a local filesystem lock.
    private sealed class Drive : HttpMessageHandler
    {
        public CloudJournal? Value;
        public bool Offline, IgnoreConditions, ApplyThenDisconnect;
        public int Revision = 1, Writes;
        private readonly object gate = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("www.googleapis.com", request.RequestUri!.Host);
            if (Offline) throw new HttpRequestException("Synthetic offline");
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (gate)
            {
                if (request.Method == HttpMethod.Post)
                {
                    if (Value is not null) return Reply(HttpStatusCode.Conflict);
                    using var create = JsonDocument.Parse(body);
                    Assert.Equal(FileId, create.RootElement.GetProperty("id").GetString());
                    Assert.Equal("drive-folder-id", create.RootElement.GetProperty("parents")[0].GetProperty("id").GetString());
                    Value = Json.Deserialize<CloudJournal>(Encoding.UTF8.GetBytes(create.RootElement.GetProperty("description").GetString()!), "synthetic");
                    return Reply(HttpStatusCode.OK);
                }
                if (Value is null) return Reply(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Get)
                    return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new
                    {
                        id = FileId, mimeType = "application/json", description = Encoding.UTF8.GetString(Json.Serialize(Value)),
                        etag = "\"" + Revision + "\"", labels = new { trashed = false },
                    }));
                Assert.Equal(HttpMethod.Patch, request.Method);
                if (!IgnoreConditions && request.Headers.IfMatch.Single().Tag != "\"" + Revision + "\"") return Reply(HttpStatusCode.PreconditionFailed);
                using var patch = JsonDocument.Parse(body);
                if (patch.RootElement.TryGetProperty("description", out var description))
                {
                    Value = Json.Deserialize<CloudJournal>(Encoding.UTF8.GetBytes(description.GetString()!), "synthetic");
                    Writes++;
                }
                Revision++;
                if (ApplyThenDisconnect && body != "{}") { ApplyThenDisconnect = false; throw new HttpRequestException("lost acknowledgement"); }
                return Reply(HttpStatusCode.OK);
            }
        }
        private static HttpResponseMessage Reply(HttpStatusCode code, string body = "{}") => new(code) { Content = new StringContent(body) };
    }

    private GoogleDriveJournal Api(Drive drive) => new(FileId, () => "synthetic-token", new HttpClient(drive, disposeHandler: false));
    private CloudJournal Initial() => new(1, new(1, LibraryId, MedalFixture.AccountId, Now, First),
        Ownership.Initial(LibraryId, MedalFixture.AccountId, Now), [], "drive-folder-id", @"M:\Clips", new string('0', 64));

    private bool failUpload;
    private readonly List<string> commands = [];
    private string Run(IReadOnlyList<string> args)
    {
        commands.Add(args[0]);
        if (args[0] == "lsjson" && args.Contains("--stat")) return """{"ID":"drive-folder-id","IsDir":true}""";
        string PathFor(string value) => value.StartsWith("gdrive:", StringComparison.Ordinal)
            ? fixture.PathFor("drive", value[(value.IndexOf(':') + 1)..].Replace('/', Path.DirectorySeparatorChar)) : value;
        string source = PathFor(args[1]);
        if (args[0] == "lsjson")
        {
            if (!Directory.Exists(source)) throw new RcloneFailedException("Synthetic missing folder", 3);
            return JsonSerializer.Serialize(Directory.GetFileSystemEntries(source, "*", SearchOption.AllDirectories).Select(p => new
            { Path = Path.GetRelativePath(source, p).Replace('\\', '/'), IsDir = Directory.Exists(p) }));
        }
        string destination = PathFor(args[2]);
        bool upload = args[2].StartsWith("gdrive:", StringComparison.Ordinal);
        if (upload && failUpload) throw new RcloneFailedException("Synthetic quota failure", 7, hints: RcloneHints.Quota);
        if (!Directory.Exists(source)) throw new RcloneFailedException("Synthetic missing folder", 3);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (args[0] == "check" || File.Exists(target)) Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(target));
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); }
        }
        return "";
    }

    private CoordinationStore Seed(Drive drive, MedalSyncConfig config, LibrarySnapshot snapshot)
    {
        var backend = new CloudCoordination(config, Api(drive), Run);
        backend.Create(snapshot, new(1, [], []), First, new string('1', 32), Versions, Now);
        return CoordinationStore.OpenCloud(config, backend);
    }

    [Fact]
    public void CloudConfigurationRequiresNoServerAndRoundTripsWithoutWeakeningExistingModes()
    {
        var config = Config("first");
        config.Validate();
        Assert.True(config.PrimaryIsRclone);
        Assert.Equal("gdrive:MedalSync/Clips", config.PrimaryCapture);
        Assert.Equal(config.Replication!.LocalCopyRoot, config.MountRemote);
        Json.Deserialize<MedalSyncConfig>(Json.Serialize(config), "config").Validate();
        Assert.Throws<InvalidDataException>((config with { CloudJournalId = null }).Validate);
        Assert.Throws<InvalidDataException>((config with { CoordinationRoot = @"\\server\journal" }).Validate);
        Assert.Throws<InvalidDataException>((config with { StorageMode = StorageMode.Server }).Validate);
        Assert.Throws<InvalidDataException>((config with { Replication = config.Replication with { LocalCopyRoot = null } }).Validate);
        Assert.Throws<InvalidDataException>((config with { CaptureFolder = @"M:\.medal-sync\Clips" }).Validate);
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, CommandRunner.Run(["config", "create", "--storage-mode", "cloud", "--account-id", "12345", "--new-library",
            "--remote", config.Remote, "--cloud-journal-id", FileId, "--local-copy-root", config.Replication.LocalCopyRoot!,
            "--rclone-config", config.Replication.RcloneConfigPath!, "--cache-root", config.CacheRoot, "--out", fixture.PathFor("candidate.json")], output, error));
        Assert.False(File.Exists(fixture.PathFor("candidate.json")));
        Assert.Contains("CLOUD", output.ToString());
    }

    [Fact]
    public void StaleConditionalClaimCannotOverwriteTheWinningPc()
    {
        using var drive = new Drive { Value = Initial() };
        var first = Api(drive); var second = Api(drive);
        var a = first.Read(); var b = second.Read();
        var winning = Ownership.Acquire(a.Value.Journal, First, new string('1', 32), Now);
        Assert.True(first.CompareExchange(a, a.Value with { Journal = winning }));
        Assert.False(second.CompareExchange(b, b.Value with { Journal = Ownership.Acquire(b.Value.Journal, Second, new string('2', 32), Now) }));
        Assert.Equal(First, drive.Value!.Journal.Owner);
        Assert.Equal(1, drive.Writes);
    }

    [Fact]
    public void EndpointThatIgnoresEtagNeverReceivesAnOwnershipMutation()
    {
        using var drive = new Drive { Value = Initial(), IgnoreConditions = true };
        var api = Api(drive); var before = api.Read();
        Assert.Throws<UnsafeOperationException>(() => api.CompareExchange(before, before.Value with
        { Journal = Ownership.Acquire(before.Value.Journal, First, new string('1', 32), Now) }));
        Assert.Equal(0, drive.Writes);
        Assert.Equal(OwnershipState.Clean, drive.Value!.Journal.State);
    }

    [Fact]
    public void LostAcknowledgementKeepsTheClaimAndNeverFallsBackToCachedReady()
    {
        using var drive = new Drive();
        var store = Seed(drive, Config("first"), Snapshot("seed", "one"));
        drive.ApplyThenDisconnect = true;
        Assert.Throws<StorageOfflineException>(() => store.Update(j => Ownership.Acquire(j, First, new string('1', 32), Now), TimeSpan.Zero));
        Assert.Equal(First, drive.Value!.Journal.Owner);
        drive.Offline = true;
        Assert.Throws<StorageOfflineException>(() => store.ReadJournal());
        drive.Offline = false;
        Assert.Equal(OwnershipState.Starting, store.ReadJournal().State);
        Assert.Throws<OwnershipConflictException>(() => store.Update(j => Ownership.Acquire(j, Second, new string('2', 32), Now), TimeSpan.Zero));
    }

    [Fact]
    public async Task SimultaneousStartsGrantExactlyOnePcAndPreserveTheLosersRequest()
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        var second = CoordinationStore.OpenCloud(Config("second"), new(Config("second"), Api(drive), Run));
        // Open both caches before racing, so only the journal CAS is under contention.
        int acquired = 0;
        async Task Claim(CoordinationStore store, DeviceIdentity device, char session)
        {
            await Task.Yield();
            try { store.Update(j => Ownership.Acquire(j, device, new string(session, 32), Now), TimeSpan.Zero); Interlocked.Increment(ref acquired); }
            catch (OwnershipConflictException) { store.WriteRequest(new(1, device, Guid.NewGuid().ToString("N"), Now)); }
        }
        await Task.WhenAll(Claim(first, First, '1'), Claim(second, Second, '2'));
        Assert.Equal(1, acquired);
        Assert.Single(first.PendingRequests(Now));
        Assert.Equal(first.ReadJournal(), second.ReadJournal());
    }

    [Fact]
    public void FailedGenerationUploadRetainsOwnershipAndAnotherPcCanResumeAfterVerifiedRetry()
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        string session = new('1', 32);
        first.Update(j => Ownership.Acquire(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.MarkActive(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.BeginDrain(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.BeginPublish(j, First, session, Now), TimeSpan.Zero);
        var next = Snapshot("next", "one", "two");
        first.Generations.Publish(1, 0, session, First, next, new(1, [], []), Versions, Now);
        failUpload = true;
        Assert.Throws<RcloneFailedException>(() => first.Update(j => Ownership.CompletePublish(j, First, session, 1, Now), TimeSpan.Zero));
        Assert.Equal(OwnershipState.Publishing, drive.Value!.Journal.State);
        Assert.Equal(0, drive.Value.Journal.CommittedGeneration);
        failUpload = false;
        first.Update(j => Ownership.CompletePublish(j, First, session, 1, Now), TimeSpan.Zero);
        Assert.Contains("check", commands);
        var second = CoordinationStore.OpenCloud(Config("second"), new(Config("second"), Api(drive), Run));
        Assert.Equal(next.Digest(), second.Generations.Read(second.ReadJournal().CommittedGeneration).Snapshot.Digest());
        Assert.Equal(2, second.Generations.Chain(1).Count());
        second.Update(j => Ownership.Acquire(j, Second, new string('2', 32), Now), TimeSpan.Zero);
        Assert.Equal(Second, first.ReadJournal().Owner);
        Assert.Throws<UnsafeOperationException>(() => second.AcquireWriterLock(TimeSpan.Zero));
    }

    [Fact]
    public void AnotherAccountOrMediaFolderIsRefusedBeforeAnyJournalWrite()
    {
        using var drive = new Drive { Value = Initial() };
        var config = Config("first") with { AccountId = "999" };
        Assert.Throws<IncompatibleLibraryException>(() => new CloudCoordination(config, Api(drive), Run).Read());
        config = Config("first");
        Assert.Throws<IncompatibleLibraryException>(() => new CloudCoordination(config, Api(drive), _ => """{"ID":"wrong-folder","IsDir":true}""").Read());
        Assert.Equal(0, drive.Writes);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("onedrive")]
    public void OnlyDirectGoogleDriveOAuthRemotesCanCoordinate(string type)
    {
        var token = new DriveAccessToken("synthetic", "gdrive:MedalSync", "private.conf", execute: _ => JsonSerializer.Serialize(new
        { gdrive = new { type, token = "{}" } }));
        Assert.Throws<UnsafeOperationException>(() => token.Get());
    }

    [Fact]
    public void ExpiredOAuthTokenRefreshesThroughRcloneWithoutReturningConfigContents()
    {
        bool refreshed = false;
        List<string> calls = [];
        var token = new DriveAccessToken("synthetic", "gdrive:MedalSync", "private.conf", execute: args =>
        {
            calls.Add(string.Join(' ', args.Take(2)));
            if (args[0] == "about") { refreshed = true; return "{}"; }
            return JsonSerializer.Serialize(new { gdrive = new { type = "drive", token = JsonSerializer.Serialize(new
            { access_token = refreshed ? "fresh-token" : "expired-token", expiry = refreshed ? Now.AddHours(1) : Now.AddHours(-1) }) } });
        });
        Assert.Equal("fresh-token", token.Get());
        Assert.Equal("fresh-token", token.Get());
        Assert.Equal(new[] { "config dump", "about gdrive:", "config dump" }, calls);
    }

    [Fact]
    public void InterruptedInitializationResumesOnlyTheSameDeviceAndSnapshot()
    {
        using var drive = new Drive();
        var config = Config("first");
        var backend = new CloudCoordination(config, Api(drive), Run);
        var snapshot = Snapshot("seed", "one");
        failUpload = true;
        Assert.Throws<RcloneFailedException>(() => backend.Create(snapshot, new(1, [], []), First, new string('1', 32), Versions, Now));
        Assert.Null(drive.Value);
        failUpload = false;
        Assert.Throws<UnsafeOperationException>(() => backend.Create(snapshot, new(1, [], []), Second, new string('2', 32), Versions, Now));
        backend.Create(snapshot, new(1, [], []), First, new string('3', 32), Versions, Now.AddMinutes(1));
        Assert.Equal(OwnershipState.Clean, backend.ReadJournal().State);
        Assert.Equal(Now, drive.Value!.Identity.CreatedAt);
    }

    [Fact]
    public void PartialAbandonedRemoteGenerationIsKeptAndItsNumberIsNeverReused()
    {
        using var drive = new Drive();
        var config = Config("first");
        var store = Seed(drive, config, Snapshot("seed", "one"));
        string orphan = fixture.PathFor("drive", "MedalSync", ".medal-sync", LibraryId, "generations", GenerationStore.Name(1));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "library.json"), "incomplete abandoned upload");
        string session = new('1', 32);
        store.Update(j => Ownership.Acquire(j, First, session, Now), TimeSpan.Zero);
        store.Update(j => Ownership.MarkActive(j, First, session, Now), TimeSpan.Zero);
        store.Update(j => Ownership.BeginDrain(j, First, session, Now), TimeSpan.Zero);
        store.Update(j => Ownership.BeginPublish(j, First, session, Now), TimeSpan.Zero);
        Assert.Equal(2, store.Generations.Next(0));
        Assert.False(store.Generations.Exists(1));
        Assert.True(File.Exists(Path.Combine(orphan, "library.json")));
        store.Generations.Publish(2, 0, session, First, Snapshot("new", "one", "two"), new(1, [], []), Versions, Now);
        store.Update(j => Ownership.CompletePublish(j, First, session, 2, Now), TimeSpan.Zero);
        var other = Config("second");
        var second = CoordinationStore.OpenCloud(other, new(other, Api(drive), Run));
        Assert.Equal(2, second.ReadJournal().CommittedGeneration);
        Assert.Equal(2, second.Generations.Chain(2).Count());
    }

    [Fact]
    public void DuplicateDriveHistoryPathsRefuseBeforeCopyOrOwnershipChanges()
    {
        using var drive = new Drive { Value = Initial() };
        var backend = new CloudCoordination(Config("first"), Api(drive), args =>
        {
            Assert.Equal("lsjson", args[0]);
            return """[{"Path":"0000000000/library.json"},{"Path":"0000000000/library.json"}]""";
        });
        Assert.Throws<InvalidDataException>(backend.Download);
        Assert.Equal(0, drive.Writes);
    }

    [Fact]
    public void CachedUnpublishedGenerationCannotMasqueradeAsAnotherPcsCommittedGenerationWithTheSameNumber()
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        var original = Snapshot("unpublished", "one", "private");
        first.Generations.Publish(1, 0, new string('1', 32), First, original, new(1, [], []), Versions, Now);
        var other = Config("second");
        var second = CoordinationStore.OpenCloud(other, new(other, Api(drive), Run));
        string session = new('2', 32);
        second.Update(j => Ownership.Acquire(j, Second, session, Now), TimeSpan.Zero);
        second.Update(j => Ownership.MarkActive(j, Second, session, Now), TimeSpan.Zero);
        second.Update(j => Ownership.BeginDrain(j, Second, session, Now), TimeSpan.Zero);
        second.Update(j => Ownership.BeginPublish(j, Second, session, Now), TimeSpan.Zero);
        second.Generations.Publish(1, 0, session, Second, Snapshot("published", "one", "shared"), new(1, [], []), Versions, Now);
        second.Update(j => Ownership.CompletePublish(j, Second, session, 1, Now), TimeSpan.Zero);
        Assert.Throws<InvalidDataException>(first.ReadJournal);
        Assert.Equal(original.Digest(), first.Generations.Read(1).Snapshot.Digest());
        Assert.Equal(OwnershipState.Clean, second.ReadJournal().State);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; private set; } = CloudHandoffTests.Now;
        public void Sleep(TimeSpan duration) => Now += duration;
    }
    private sealed class Medal : IMedalProcess
    {
        public bool Running;
        public bool IsRunning() => Running;
        public void Launch() => Running = true;
    }
    private sealed class Mount : IClipMount
    {
        public bool IsRunning { get; private set; }
        public void Start(string session, bool forRecording) => IsRunning = true;
        public MountHealth Health() => new(true, "synthetic");
        public DrainVerdict Drain() => new(DrainState.Drained, [], 0, 0);
        public bool Stop() { IsRunning = false; return true; }
        public void Forget(string session) { }
        public void RetainAbortedStart(string session) { }
    }
    private sealed class Assets : IAssetChecker
    {
        private static AssetReport Report(LibrarySnapshot snapshot) => new(AssetInventory.Collect(snapshot)
            .Select(r => new AssetStatus(r, AssetProblem.None, 1, DateTime.UnixEpoch, null)).ToArray());
        public AssetReport ThroughMount(LibrarySnapshot snapshot) => Report(snapshot);
        public AssetReport OnServer(LibrarySnapshot snapshot) => Report(snapshot);
        public bool ExistsOnServer(string capturePath) => true;
        public void RequireServer() { }
    }

    [Fact]
    public void NormalCoordinatorLaunchFinishAndSecondPcRestoreUseCloudOwnership()
    {
        using var drive = new Drive();
        var config = Config("first");
        var store = Seed(drive, config, Snapshot("seed", "one"));
        var otherConfig = Config("second");
        var otherStore = CoordinationStore.OpenCloud(otherConfig, new(otherConfig, Api(drive), Run));
        var clock = new Clock();
        var firstMedal = new Medal(); var secondMedal = new Medal();
        string firstDb = fixture.CreateLibrary("first-db"), secondDb = fixture.CreateLibrary("second-db");
        using (var connection = MedalFixture.Open(secondDb)) MedalFixture.SetSetting(connection, "ClipFolder", JsonSerializer.Serialize(@"C:\OriginalClips"));
        string settingsBefore = MedalFixture.Settings(secondDb);
        SessionCoordinator Pc(CoordinationStore journal, DeviceIdentity device, string db, Medal medal, string name) => new(journal,
            new SqliteLocalLibrary(db, MedalFixture.AccountId, fixture.PathFor(name, "backups"), clock, @"M:\Clips"), new Mount(), medal,
            new Assets(), clock, new(device, config.Capture, Versions, TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1)));
        var first = Pc(store, First, firstDb, firstMedal, "first");
        var second = Pc(otherStore, Second, secondDb, secondMedal, "second");
        var started = first.Launch();
        Assert.Equal(LaunchStatus.Started, started.Status);
        Assert.Equal(LaunchStatus.Waiting, second.Launch().Status);
        MedalFixture.AddContent(firstDb, "two", video: @"M:\Clips\two.mp4");
        firstMedal.Running = false;
        Assert.Equal(FinishStatus.Published, first.Finish(started.Session!).Status);
        Assert.Equal(LaunchStatus.Started, second.Launch().Status);
        Assert.Equal(2, LibraryDatabase.ReadSnapshot(secondDb, MedalFixture.AccountId).RowCount("contents"));
        var library = new SqliteLocalLibrary(secondDb, MedalFixture.AccountId, fixture.PathFor("unused"), clock);
        Assert.Equal(@"M:\Clips\", library.CaptureFolder());
        using (var connection = MedalFixture.Open(secondDb)) MedalFixture.SetSetting(connection, "ClipFolder", JsonSerializer.Serialize(@"C:\OriginalClips"));
        Assert.Equal(settingsBefore, MedalFixture.Settings(secondDb)); // All other device settings survived the restore.
        Assert.NotEmpty(DatabaseBackup.List(fixture.PathFor("second", "backups"), MedalFixture.AccountId));
    }
}

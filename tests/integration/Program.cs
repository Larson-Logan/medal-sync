using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
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

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int _checks;
    // Synthetic installation manifests model an explicitly reviewed build.
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");

    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        // This separate test executable is used only by a uniquely identified native test installer.
        // The production binaries have no flag or environment variable that bypasses readiness.
        if (args is ["setup", "check", ..]) return ProbeSetup(args);
        if (args is ["--update-progress"]) return ProbeUpdateProgress();
        try
        {
            string rclone = args[Array.IndexOf(args, "--rclone") + 1];
            if (!File.Exists(rclone)) throw new IOException("An explicit pinned rclone executable is required.");
            TestReplication(rclone);
            TestIncrementalAndReadyRetry(rclone);
            TestReplicaAuditAndPrune(rclone);
            TestCloudPrimary(rclone);
            TestInterruptedTransfer(rclone);
            if (args.Contains("--mount"))
            {
                TestTwoPcMountRoundTrip(rclone);
                TestTwoPcMountRoundTrip(rclone, localOnly: true);
            }
            Console.WriteLine($"PASS: {_checks} real-process storage integration assertions; all data was synthetic.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int ProbeUpdateProgress()
    {
        using var fixture = new MedalFixture();
        byte[] bytes = Encoding.UTF8.GetBytes("Synthetic update payload; never executed.");
        var asset = new ReleaseAsset("0.5.0", "medal-sync-0.5.0-setup-win-x64.exe",
            "https://github.com/Larson-Logan/medal-sync/releases/download/v0.5.0/medal-sync-0.5.0-setup-win-x64.exe",
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        using var client = new HttpClient(new UpdateHandler(bytes));
        string path = WindowsReleaseUpdate.Download(asset, fixture.PathFor("download"), client);
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "native Shell update progress completes with a verified synthetic download");
        return 0;
    }

    private sealed class UpdateHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(500, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }

    private static int ProbeSetup(string[] args)
    {
        string root = args[Array.IndexOf(args, "--state-root") + 1];
        string report = args[Array.IndexOf(args, "--out") + 1];
        string message = "Ready for isolated native setup.";
        int result = 0;
        try
        {
            SetupMaintenance.RequireReady(root, File.Exists(Path.Combine(root, "application-running")), File.Exists(Path.Combine(root, "supervisor-running")));
            // Model a state change while the uninstall confirmation is open.
            if (File.Exists(Path.Combine(root, "refuse-second-check")))
            {
                string first = Path.Combine(root, "first-check-seen");
                if (File.Exists(first)) throw new UnsafeOperationException("Synthetic library became busy after the first readiness check.");
                File.WriteAllText(first, "ready");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MedalSyncException or UnauthorizedAccessException)
        { result = 1; message = ex.Message; }
        using var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.Write(message);
        return result;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS: " + message);
    }

    private static MedalSyncConfig Config(MedalFixture fixture, string rclone, string capture = @"Q:\Clips") => new(1,
        "0123456789abcdef0123456789abcdef", MedalFixture.AccountId, fixture.PathFor("journal"), fixture.PathFor("server"), capture,
        fixture.PathFor("cache"), rclone, new MountConfig(1, 1, 1, 60, 1, 1, 2, FreePort()), .001, [])
    { StorageMode = StorageMode.Local };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Remotes(MedalFixture fixture)
    {
        string file = fixture.PathFor("synthetic.rclone.conf");
        File.WriteAllText(file, string.Join("\n", new[] { "onedrive", "dropbox", "gdrive" }.Select(name =>
            $"[{name}]\ntype = alias\nremote = {fixture.PathFor(name)}\n")));
        return file;
    }

    private static ReplicaTarget[] Targets() => [new("onedrive", "onedrive:MedalSync") { Required = true },
        new("dropbox", "dropbox:MedalSync") { Required = true }, new("google", "gdrive:MedalSync") { Required = true }];

    private static string ReplicaFile(MedalFixture fixture, string remote, string file) =>
        fixture.PathFor(remote, "MedalSync", "0123456789abcdef0123456789abcdef", file);

    private static void Seed(MedalSyncConfig config)
    {
        Directory.CreateDirectory(config.ServerCapture.Display);
        File.WriteAllText(config.ServerCapture.Append(MedalSyncConfig.StorageMarker).Display, config.LibraryId);
        File.WriteAllBytes(config.ServerCapture.Append("#clip [test].mp4").Display, Encoding.UTF8.GetBytes("synthetic media bytes"));
        File.WriteAllText(config.ServerCapture.Append("private.db").Display, "private synthetic sentinel");
    }

    private static void TestReplication(string rclone)
    {
        using var fixture = new MedalFixture();
        var config = Config(fixture, rclone) with { Replication = new(fixture.PathFor("persistent"), Remotes(fixture), Targets()) };
        // Server-mode paths here are disposable local trees; no live share is accessed.
        config = config with { StorageMode = StorageMode.Server };
        Seed(config);
        string db = fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "clip", video: config.Capture.Append("#clip [test].mp4").Display);
        var snapshot = LibraryDatabase.ReadSnapshot(db, config.AccountId);
        var state = new LocalState(fixture.PathFor("state"));
        var replication = new StorageReplication(config, state, () => { });
        replication.Prepare(snapshot);
        Check(File.Exists(config.MountedCapture.Append("#clip [test].mp4").Display), "hydrate persistent local media with a real rclone process");
        Check(!File.Exists(config.MountedCapture.Append("private.db").Display), "hydration excludes unrelated private files");
        replication.Publish(snapshot);
        foreach (string remote in new[] { "onedrive", "dropbox", "gdrive" })
        {
            Check(File.ReadAllBytes(ReplicaFile(fixture, remote, "#clip [test].mp4")).SequenceEqual(Encoding.UTF8.GetBytes("synthetic media bytes")), "verified media copied through named " + remote + " test remote");
            Check(!File.Exists(ReplicaFile(fixture, remote, "private.db")), remote + " test remote excludes database sentinel");
        }
        string receipt = Path.Combine(state.Root, "replication", "last-verified.json");
        byte[] priorReceipt = File.ReadAllBytes(receipt);
        File.WriteAllText(ReplicaFile(fixture, "onedrive", "#clip [test].mp4"), "conflicting destination");
        bool refused = false;
        try { replication.Publish(snapshot); } catch (IOException) { refused = true; }
        Check(refused, "real rclone refuses a conflicting destination");
        Check(priorReceipt.SequenceEqual(File.ReadAllBytes(receipt)), "failed replication does not replace the last verified receipt");
        Check(File.Exists(config.MountedCapture.Append("#clip [test].mp4").Display), "failed replication retains persistent local media");
        File.Delete(ReplicaFile(fixture, "onedrive", "#clip [test].mp4")); // Synthetic conflict only.
        replication.Publish(snapshot);
        Check(File.ReadAllBytes(ReplicaFile(fixture, "onedrive", "#clip [test].mp4")).SequenceEqual(Encoding.UTF8.GetBytes("synthetic media bytes")), "retry repairs the resolved synthetic destination");
        Check(!Directory.EnumerateFiles(state.Root, "replication-*.txt").Any(), "private transfer file lists are cleaned after success and failure");
        File.WriteAllText(ReplicaFile(fixture, "onedrive", "#clip [test].mp4"), "prior destination version");
        var versioned = config with { Replication = config.Replication! with { ConflictPolicy = ReplicaConflictPolicy.Version } };
        new StorageReplication(versioned, state, () => { }).Publish(snapshot);
        string history = Path.Combine(Path.GetDirectoryName(ReplicaFile(fixture, "onedrive", "#clip [test].mp4"))!, ".medal-sync-history");
        Check(Directory.EnumerateFiles(history, "*.mp4", SearchOption.AllDirectories).Any(f => File.ReadAllText(f) == "prior destination version"), "versioned replication preserves the previous destination before updating");
        Check(File.ReadAllText(ReplicaFile(fixture, "onedrive", "#clip [test].mp4")) == "synthetic media bytes", "versioned replication verifies the updated destination");
    }

    private static void TestIncrementalAndReadyRetry(string rclone)
    {
        using var fixture = new MedalFixture();
        var config = Config(fixture, rclone) with { Replication = new(null, Remotes(fixture), [new("cloud", "onedrive:MedalSync")])
            { FullVerifyEveryHandoffs = 3 } };
        Seed(config);
        string db = fixture.CreateLibrary("incremental");
        MedalFixture.AddContent(db, "one", video: config.Capture.Append("#clip [test].mp4").Display);
        var snapshot = LibraryDatabase.ReadSnapshot(db, config.AccountId);
        var state = new LocalState(fixture.PathFor("state"));
        List<IReadOnlyList<string>> commands = [];
        void Execute(IReadOnlyList<string> arguments)
        {
            commands.Add(arguments);
            new RcloneRunner(rclone).Run(arguments, TimeSpan.FromSeconds(30));
        }
        var replication = new StorageReplication(config, state, () => { }, Execute);
        Check(replication.Publish(snapshot).ReadBackFiles == 1, "incremental first pass byte-verifies every file using real rclone");
        commands.Clear();
        Check(replication.Publish(snapshot).ReadBackFiles == 0 && commands.Count == 1 && commands[0].Contains("--size-only")
            && !commands[0].Contains("--download"), "unchanged library performs only a presence/size check without media read-back");
        File.WriteAllText(config.ServerCapture.Append("new.mp4").Display, "new synthetic media");
        MedalFixture.AddContent(db, "two", video: config.Capture.Append("new.mp4").Display);
        snapshot = LibraryDatabase.ReadSnapshot(db, config.AccountId);
        Check(replication.Publish(snapshot).ReadBackFiles == 1, "one added clip reads back only its bytes while old clips get a size check");
        string destination = ReplicaFile(fixture, "onedrive", "#clip [test].mp4");
        File.WriteAllText(destination, new string('x', "synthetic media bytes".Length));
        Check(replication.Publish(snapshot).Deferred.SequenceEqual(["cloud"]), "scheduled full check catches same-size corruption of previously unchanged media");
        File.Delete(destination);
        var store = CoordinationStore.Create(config.LibraryCoordinationDirectory, config.LibraryId, snapshot, new(1, [], []),
            new(new string('d', 32), "Synthetic PC"), new string('e', 32), Versions, DateTimeOffset.UtcNow);
        var before = store.ReadJournal();
        var retry = new ReadyReplicaRetry(config, state, store, () => { }, Execute);
        Check(retry.Plan().Targets.SequenceEqual(["cloud"]), "Ready retry previews the exact pending optional target");
        Check(retry.Apply(retry.Plan()).Deferred.Count == 0 && StorageReplication.Pending(state, config.LibraryId).Count == 0
            && File.ReadAllText(destination) == "synthetic media bytes", "Ready retry copies and byte-verifies a repaired target and clears pending evidence");
        Check(store.ReadJournal() == before, "real Ready retry never acquires ownership or changes the journal");
        File.Delete(destination);
        Check(replication.Publish(snapshot).Deferred.SequenceEqual(["cloud"]), "incremental size check catches deleted unchanged media");
        Check(retry.Apply(retry.Plan()).ReadBackFiles == 2, "failed listing invalidates receipts so Ready retry verifies the whole destination");
    }

    private static void TestReplicaAuditAndPrune(string rclone)
    {
        using var fixture = new MedalFixture();
        var config = Config(fixture, rclone) with { Replication = new(null, Remotes(fixture), [new("cloud", "onedrive:MedalSync")]) };
        Seed(config);
        string db = fixture.CreateLibrary("audit");
        MedalFixture.AddContent(db, "one", video: config.Capture.Append("#clip [test].mp4").Display);
        var snapshot = LibraryDatabase.ReadSnapshot(db, config.AccountId);
        var state = new LocalState(fixture.PathFor("state"));
        new StorageReplication(config, state, () => { }).Publish(snapshot);
        var store = CoordinationStore.Create(config.LibraryCoordinationDirectory, config.LibraryId, snapshot, new(1, [], []),
            new(new string('d', 32), "Synthetic PC"), new string('e', 32), Versions, DateTimeOffset.UtcNow.AddDays(-100));
        var before = store.ReadJournal();
        var remote = new RcloneReplicaRemote(config, state, () => { });
        var audit = new ReplicaAudit(config, state, store, () => { }, remote);
        string destination = ReplicaFile(fixture, "onedrive", "#clip [test].mp4");
        string folder = Path.GetDirectoryName(destination)!;

        var passed = audit.Apply(audit.Plan()).Single();
        Check(passed.Result == ReplicaAudit.Passed && passed.Matched == 1, "real rclone check reads back the committed generation and passes");
        File.WriteAllText(destination, "different synthetic bytes");
        var different = audit.Apply(audit.Plan()).Single();
        Check(different.Result == ReplicaAudit.Failed && different.Mismatched == 1 && File.ReadAllText(destination) == "different synthetic bytes",
            "real rclone check reports a different file and never repairs it");
        File.Delete(destination);
        var missing = audit.Apply(audit.Plan()).Single();
        Check(missing.Result == ReplicaAudit.Failed && missing.Missing == 1 && !File.Exists(destination), "real rclone check reports a missing file and never copies it");
        Check(StorageReplication.Pending(state, config.LibraryId).Single().Name == "cloud", "a failed check queues the optional target for a Ready retry");
        Check(store.ReadJournal() == before, "replica checks never acquire ownership or change the journal");
        File.WriteAllText(destination, "synthetic media bytes");

        string stale = Path.Combine(folder, "old [clip] #2.mp4");
        File.WriteAllText(stale, "unreferenced synthetic bytes");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-60));
        string recent = Path.Combine(folder, "recent.mp4");
        File.WriteAllText(recent, "recent synthetic bytes");
        var prune = new ReplicaPrune(config, state, store, () => { }, remote);
        var plan = prune.Plan("cloud");
        Check(plan.Files.Select(f => f.Path).SequenceEqual(["old [clip] #2.mp4"]), "real listing plans only the old unreferenced file");
        var result = prune.Apply(plan);
        string trashed = Path.Combine(folder, ".medal-sync-trash", plan.Id, "old [clip] #2.mp4");
        Check(result.Moved == 1 && !File.Exists(stale) && File.ReadAllText(trashed) == "unreferenced synthetic bytes"
            && File.Exists(destination) && File.Exists(recent), "real rclone moves exactly the reviewed file into the destination trash");
        Check(prune.Trash("cloud").Single().Name == plan.Id, "trash batches are listed by review ID");
        Check(prune.Purge("cloud", ReplicaPrune.DefaultPurgeDays, apply: true).Count == 0 && File.Exists(trashed), "new trash is kept by purge");
        new ReplicaPrune(config, state, store, () => { }, remote, () => DateTimeOffset.UtcNow.AddDays(8)).Purge("cloud", ReplicaPrune.DefaultPurgeDays, apply: true);
        Check(!Directory.Exists(Path.Combine(folder, ".medal-sync-trash", plan.Id)) && File.Exists(destination) && File.Exists(recent),
            "real rclone purge deletes only old trash");
        Check(remote.List("onedrive:Absent/folder").Count == 0, "a destination folder that does not exist lists as empty");
    }

    private static void TestCloudPrimary(string rclone)
    {
        using var fixture = new MedalFixture();
        var config = Config(fixture, rclone) with
        {
            StorageMode = StorageMode.Server, Remote = "onedrive:Primary",
            Replication = new(fixture.PathFor("persistent"), Remotes(fixture), [new("optional", "gdrive:Replica")]),
        };
        var state = new LocalState(fixture.PathFor("state"));
        var checker = new RcloneAssetChecker(config, state);
        checker.Initialize(false);
        Check(File.ReadAllText(fixture.PathFor("onedrive", "Primary", "Clips", MedalSyncConfig.StorageMarker)).Trim() == config.LibraryId,
            "real named primary initializes and reads back its immutable library marker");
        Directory.CreateDirectory(config.MountedCapture.Display);
        File.WriteAllText(config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display, config.LibraryId);
        File.WriteAllText(config.MountedCapture.Append("clip.mp4").Display, "synthetic primary media");
        string db = fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "clip", video: config.Capture.Append("clip.mp4").Display);
        var snapshot = LibraryDatabase.ReadSnapshot(db, config.AccountId);
        var replication = new StorageReplication(config, state, () => { });
        replication.Prepare(snapshot);
        replication.Publish(snapshot);
        Check(File.ReadAllText(fixture.PathFor("onedrive", "Primary", "Clips", "clip.mp4")) == "synthetic primary media",
            "real named primary receives and byte-verifies local-only media");
        string optional = fixture.PathFor("gdrive", "Replica", config.LibraryId, "clip.mp4");
        File.WriteAllText(optional, "synthetic conflict");
        Check(replication.Publish(snapshot).Deferred.SequenceEqual(["optional"]) && StorageReplication.Pending(state, config.LibraryId).Count == 1,
            "real optional replica conflict is deferred with pending evidence after primary verification");
        File.Delete(optional); // Disposable synthetic destination only.
        replication.Publish(snapshot);
        Check(StorageReplication.Pending(state, config.LibraryId).Count == 0 && File.ReadAllText(optional) == "synthetic primary media",
            "real optional replica retry verifies bytes and clears pending evidence");
        var migrated = config with { Remote = "dropbox:NewPrimary" };
        new RcloneAssetChecker(migrated, state).Initialize(false);
        new StorageReplication(migrated, state, () => { }).Prepare(snapshot);
        new StorageReplication(migrated, state, () => { }).Publish(snapshot);
        Check(File.ReadAllText(fixture.PathFor("dropbox", "NewPrimary", "Clips", "clip.mp4")) == "synthetic primary media"
            && File.Exists(fixture.PathFor("onedrive", "Primary", "Clips", "clip.mp4")),
            "switching to an empty named primary verifies all media and preserves the old primary");
        var second = migrated with { Replication = migrated.Replication! with { LocalCopyRoot = fixture.PathFor("second-local") } };
        new StorageReplication(second, new(fixture.PathFor("second-state")), () => { }).Prepare(snapshot);
        Check(File.ReadAllText(second.MountedCapture.Append("clip.mp4").Display) == "synthetic primary media",
            "a second local collection hydrates missing bytes from the real named primary");
    }

    private static void TestInterruptedTransfer(string rclone)
    {
        using var fixture = new MedalFixture();
        var config = Config(fixture, rclone) with { Replication = new(null, Remotes(fixture), [Targets()[0]]) { BandwidthLimitMiB = 1 } };
        config.Validate();
        Seed(config);
        File.WriteAllBytes(config.ServerCapture.Append("large.mp4").Display, new byte[8 * 1024 * 1024]);
        string db = fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "clip", video: config.Capture.Append("large.mp4").Display);
        var snapshot = LibraryDatabase.ReadSnapshot(db, config.AccountId);
        var state = new LocalState(fixture.PathFor("state"));
        DateTime cancelAt = DateTime.UtcNow.AddMilliseconds(500);
        var interrupted = new StorageReplication(config, state, () => { if (DateTime.UtcNow >= cancelAt) throw new OperationCanceledException("synthetic interruption"); });
        bool canceled = false;
        try { interrupted.Publish(snapshot); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "a running real rclone transfer honors cancellation");
        Check(!File.Exists(Path.Combine(state.Root, "replication", "last-verified.json")), "interruption produces no successful verification receipt");
        Check(File.Exists(config.ServerCapture.Append("large.mp4").Display), "interruption retains the full local source");
        config = config with { Replication = config.Replication! with { BandwidthLimitMiB = null } };
        new StorageReplication(config, state, () => { }).Publish(snapshot);
        Check(Hashing.Sha256File(ReplicaFile(fixture, "onedrive", "large.mp4")) == Hashing.Sha256File(config.ServerCapture.Append("large.mp4").Display), "interrupted transfer retries to a verified complete copy");
    }

    private sealed class SyntheticMedal : IMedalProcess
    {
        public bool Running { get; set; }
        public bool IsRunning() => Running;
        public void Launch() => Running = true;
    }

    private static void TestTwoPcMountRoundTrip(string rclone, bool localOnly = false)
    {
        var fixture = new MedalFixture();
        RcloneClipMount? mount = null;
        try
        {
            char letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Reverse().First(c =>
                !Directory.Exists(c + @":\") && !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase)));
            var config = Config(fixture, rclone, letter + @":\Clips") with
            { Replication = new(fixture.PathFor("first-persistent"), Remotes(fixture), Targets()), StorageMode = StorageMode.Server };
            if (localOnly)
            {
                config = config with { StorageMode = StorageMode.Local, Replication = config.Replication! with { LocalCopyRoot = null } };
                config.Validate();
            }
            Seed(config);
            string seed = fixture.CreateLibrary("seed");
            MedalFixture.AddContent(seed, "clip", video: config.Capture.Append("#clip [test].mp4").Display);
            var snapshot = LibraryDatabase.ReadSnapshot(seed, config.AccountId);
            var firstState = new LocalState(fixture.PathFor("first-state"));
            var secondState = new LocalState(fixture.PathFor("second-state"));
            var clock = new SystemClock();
            var store = CoordinationStore.Create(config.LibraryCoordinationDirectory, config.LibraryId, snapshot, new AssetManifest(1, [], []),
                firstState.Device(), SessionCoordinator.NewId(), Versions, clock.Now);
            for (int pc = 0; pc < (localOnly ? 1 : 2); pc++)
            {
                var state = pc == 0 ? firstState : secondState;
                if (pc == 1) config = config with { Replication = config.Replication! with { LocalCopyRoot = fixture.PathFor("second-persistent") }, Mount = config.Mount with { ControlPort = FreePort() } };
                string db = fixture.CreateLibrary(pc == 0 ? "first" : "second");
                using (var connection = MedalFixture.Open(db)) MedalFixture.SetSetting(connection, "ClipFolder", JsonSerializer.Serialize(config.Capture.Display));
                var medal = new SyntheticMedal();
                string installDirectory = fixture.PathFor("synthetic-install-" + pc);
                Directory.CreateDirectory(Path.Combine(installDirectory, "current"));
                File.WriteAllText(Path.Combine(installDirectory, "current", "Medal.exe"), "synthetic; never executed");
                void AppVersion(string version) => File.WriteAllText(Path.Combine(installDirectory, "current", "sq.version"),
                    $"<package><metadata><version>{version}</version></metadata></package>");
                AppVersion(Versions.App!);
                File.WriteAllText(Path.Combine(installDirectory, "version.json"), JsonSerializer.Serialize(new { RecorderHandler2Version = Versions.Recorder }));
                var compatibility = new MedalCompatibilityGuard(MedalEnvironment.Locate(installDirectory, Path.GetDirectoryName(db)!));
                mount = new RcloneClipMount(config, state, .01, clock);
                var coordinator = new SessionCoordinator(store, new SqliteLocalLibrary(db, config.AccountId, state.BackupDirectory, clock),
                    mount, medal, new ShareAssetChecker(config.Capture, config.ServerCapture, config.LibraryId, new FileSystemProbe()), clock,
                    new SessionOptions(state.Device(), config.Capture, Versions, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250)),
                    replication: new StorageReplication(config, state, () => { if (medal.Running) throw new IOException("Synthetic Medal reopened."); }),
                    compatibility: compatibility);
                if (pc == 0)
                {
                    AppVersion("update-in-progress");
                    Check(coordinator.Launch().Status == LaunchStatus.Blocked && !mount.IsRunning && store.ReadJournal().State == OwnershipState.Clean,
                        "unreadable update metadata refuses startup before acquiring ownership or mounting storage");
                    AppVersion("9999.1.1");
                    using (var connection = MedalFixture.Open(db)) MedalFixture.Execute(connection, "PRAGMA user_version=5");
                    Check(coordinator.Launch().Status == LaunchStatus.Blocked && !mount.IsRunning && store.ReadJournal().State == OwnershipState.Clean,
                        "an updated unsupported schema refuses startup before acquiring ownership or mounting storage");
                    using (var connection = MedalFixture.Open(db)) MedalFixture.Execute(connection, "PRAGMA user_version=4");
                }
                var launch = coordinator.Launch();
                Check(launch.Status == LaunchStatus.Started, "PC " + (pc + 1) + " mounts real WinFsp storage and restores the committed synthetic library: " + launch.Message);
                Check(File.ReadAllBytes(config.Capture.Append("#clip [test].mp4").Display).SequenceEqual(Encoding.UTF8.GetBytes("synthetic media bytes")), "PC " + (pc + 1) + " reads committed media through the real mount");
                if (pc == 0)
                {
                    File.WriteAllBytes(config.Capture.Append("new.mp4").Display, Encoding.UTF8.GetBytes("new synthetic recording"));
                    MedalFixture.AddContent(db, "new", video: config.Capture.Append("new.mp4").Display);
                }
                else Check(File.ReadAllText(config.Capture.Append("new.mp4").Display) == "new synthetic recording", "second PC hydrates and reads the first PC's published recording");
                medal.Running = false;
                if (pc == 0)
                {
                    AppVersion("update-in-progress");
                    Check(compatibility.CheckForUpdate()?.Contains("handoff is blocked", StringComparison.Ordinal) == true,
                        "fresh synthetic update metadata warns that handoff is blocked");
                    var blocked = coordinator.Finish(launch.Session!);
                    Check(blocked.Status == FinishStatus.Blocked && mount.Drain().State == DrainState.Drained,
                        "incompatible Medal update still drains real rclone uploads before blocking publication");
                    var retained = store.ReadJournal();
                    Check(retained.State == OwnershipState.Draining && retained.Owner == state.Device() && retained.CommittedGeneration == 0,
                        "incompatible update retains exact ownership and the committed generation");
                    Check(mount.IsRunning && Directory.Exists(config.SessionCacheDirectory(launch.Session!)),
                        "incompatible update retains the real WinFsp mount and session cache");
                    Check(File.ReadAllText(config.MountedCapture.Append("new.mp4").Display) == "new synthetic recording",
                        "the drained synthetic recording remains intact in the mount's backing storage while publication is blocked");
                    AppVersion("9999.1.2");
                    File.WriteAllText(Path.Combine(installDirectory, "version.json"), JsonSerializer.Serialize(new { RecorderHandler2Version = "9999.2.2" }));
                    Check(compatibility.CheckForUpdate() is null, "completed app and recorder updates with the supported schema need no per-version approval");
                }
                var finish = coordinator.Finish(launch.Session!);
                Check(finish.Status == (pc == 0 ? FinishStatus.Published : FinishStatus.Unchanged), "PC " + (pc + 1) + " completes verified handoff: " + finish.Message);
                if (pc == 0) Check(store.Generations.Read(1).Manifest.Medal == new MedalVersions("9999.1.2", "9999.2.2"),
                    "retry after a routine update publishes its freshly detected app and recorder versions");
                Check(store.ReadJournal().State == OwnershipState.Clean, "PC " + (pc + 1) + " releases ownership only after verified copies");
                Check(!Directory.Exists(config.SessionCacheDirectory(launch.Session!)), "PC " + (pc + 1) + " clears only its finished session cache");
                Check(File.Exists(config.MountedCapture.Append("new.mp4").Display), "PC " + (pc + 1) + " retains the persistent local collection");
                mount = null;
            }
            foreach (string remote in new[] { "onedrive", "dropbox", "gdrive" })
                Check(File.ReadAllText(ReplicaFile(fixture, remote, "new.mp4")) == "new synthetic recording", "published recording reaches the named " + remote + " test remote");
            if (localOnly) Check(!config.Remote.StartsWith(@"\\", StringComparison.Ordinal) && !config.CoordinationRoot.StartsWith(@"\\", StringComparison.Ordinal), "local-plus-cloud recording completes without SMB storage or coordination");
        }
        finally
        {
            if (mount is null || !mount.IsRunning || mount.Stop()) fixture.Dispose();
            else Console.Error.WriteLine("Synthetic mount did not stop; retaining test evidence at " + fixture.Root);
        }
    }
}

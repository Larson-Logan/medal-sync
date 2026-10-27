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

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int _checks;
    private static readonly MedalVersions Versions = new("synthetic", "synthetic");

    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        // This separate test executable is used only by a uniquely identified native test installer.
        // The production binaries have no flag or environment variable that bypasses readiness.
        if (args is ["setup", "check", ..]) return ProbeSetup(args);
        try
        {
            string rclone = args[Array.IndexOf(args, "--rclone") + 1];
            if (!File.Exists(rclone)) throw new IOException("An explicit pinned rclone executable is required.");
            TestReplication(rclone);
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

    private static int ProbeSetup(string[] args)
    {
        string root = args[Array.IndexOf(args, "--state-root") + 1];
        string report = args[Array.IndexOf(args, "--out") + 1];
        string message = "Ready for isolated native setup.";
        int result = 0;
        try { SetupMaintenance.RequireReady(root, File.Exists(Path.Combine(root, "application-running")), File.Exists(Path.Combine(root, "supervisor-running"))); }
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

    private static ReplicaTarget[] Targets() => [new("onedrive", "onedrive:MedalSync"), new("dropbox", "dropbox:MedalSync"), new("google", "gdrive:MedalSync")];

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
                mount = new RcloneClipMount(config, state, .01, clock);
                var coordinator = new SessionCoordinator(store, new SqliteLocalLibrary(db, config.AccountId, state.BackupDirectory, clock),
                    mount, medal, new ShareAssetChecker(config.Capture, config.ServerCapture, config.LibraryId, new FileSystemProbe()), clock,
                    new SessionOptions(state.Device(), config.Capture, Versions, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250)),
                    replication: new StorageReplication(config, state, () => { if (medal.Running) throw new IOException("Synthetic Medal reopened."); }));
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
                var finish = coordinator.Finish(launch.Session!);
                Check(finish.Status == (pc == 0 ? FinishStatus.Published : FinishStatus.Unchanged), "PC " + (pc + 1) + " completes verified handoff: " + finish.Message);
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

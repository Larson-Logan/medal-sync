using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
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

namespace MedalSync.Integration;

/// <summary>
/// A second PC with its own clips enrolls into a library whose primary is a named rclone remote (a local alias here).
/// Real rclone lists, uploads and byte-verifies; only the following finish may publish the union and release the library.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CloudEnrollment
{
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");

    internal static void Run(string rclone, Action<bool, string> check)
    {
        using var fixture = new MedalFixture();
        string credentials = fixture.PathFor("synthetic.rclone.conf");
        Directory.CreateDirectory(fixture.PathFor("onedrive"));
        File.WriteAllText(credentials, "[onedrive]\ntype = alias\nremote = " + fixture.PathFor("onedrive") + "\n");
        char letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Reverse().First(c =>
            !Directory.Exists(c + @":\") && !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase)));
        var first = new MedalSyncConfig(1, "0123456789abcdef0123456789abcdef", MedalFixture.AccountId, fixture.PathFor("journal"),
            "onedrive:Primary", letter + @":\Clips", fixture.PathFor("first-cache"), rclone, new MountConfig(1, 1, 1, 60, 1, 1, 2, FreePort()), .001, [])
        { Replication = new(fixture.PathFor("first-local"), credentials, []) };
        var firstPc = new DeviceIdentity(new string('a', 32), "Synthetic first PC");
        var secondPc = new DeviceIdentity(new string('b', 32), "Synthetic second PC");

        // The first PC creates the library on the named primary from its local copy.
        var firstState = new LocalState(fixture.PathFor("first-state"));
        new RcloneAssetChecker(first, firstState).Initialize(false);
        StorageIdentity.Initialize(first.MountedCapture.Display, first.LibraryId);
        File.WriteAllText(first.MountedCapture.Append("shared.mp4").Display, "Synthetic shared clip from the first PC.");
        string firstDb = fixture.CreateLibrary("first");
        MedalFixture.AddContent(firstDb, "shared", video: first.Capture.Append("shared.mp4").Display);
        var shared = LibraryDatabase.ReadSnapshot(firstDb, first.AccountId);
        new StorageReplication(first, firstState, () => { }).Publish(shared);
        var store = CoordinationStore.Create(first.LibraryCoordinationDirectory, first.LibraryId, shared,
            AssetManifest.From(new RcloneAssetChecker(first, firstState).OnServer(shared)), firstPc, SessionCoordinator.NewId(), Versions, DateTimeOffset.UtcNow);

        // The second PC has its own recording in its original capture folder and an empty local copy.
        var second = first with { Replication = first.Replication! with { LocalCopyRoot = fixture.PathFor("second-local") },
            CacheRoot = fixture.PathFor("second-cache"), Mount = first.Mount with { ControlPort = FreePort() } };
        var secondState = new LocalState(fixture.PathFor("second-state"));
        string secondDb = fixture.CreateLibrary("second");
        string original = fixture.PathFor("second-original");
        Directory.CreateDirectory(original);
        string recording = Path.Combine(original, "own [clip].mp4");
        File.WriteAllText(recording, "Synthetic recording made on the second PC before it joined.");
        using (var db = MedalFixture.Open(secondDb)) MedalFixture.SetSetting(db, "ClipFolder", JsonSerializer.Serialize(original));
        MedalFixture.AddContent(secondDb, "own", video: recording, metadata: "{\"title\":\"Own\"}");
        var local = LibraryDatabase.ReadSnapshot(secondDb, second.AccountId);
        var destination = second.Capture.Append("Devices", secondPc.DeviceId);
        var plan = LibraryReconciliation.Plan(shared, local, WindowsPath.Parse(original), destination);
        var candidate = CountRebuilder.Rebuild(plan.Snapshot, secondDb);
        var primary = new RcloneAssetChecker(second, secondState);
        var conflicts = EnrollmentCommands.LocalCopyConflicts(second, primary, plan.Files);
        check(plan.Conflicts.Count == 0 && conflicts.Count == 0 && plan.Added == 1 && candidate.RowCount("contents") == 2,
            "cloud-primary enrollment previews a lossless union and lists the real named primary for conflicts");
        string uploaded = fixture.PathFor("onedrive", "Primary", "Clips", "Devices", secondPc.DeviceId, "own [clip].mp4");
        var review = new EnrollmentReview(1, second.LibraryId, secondPc.DeviceId, 0, shared.Digest(), local.Digest(), candidate.Digest(),
            original, destination.Display, plan.Added, plan.Matched, plan.Identities, plan.Files, conflicts);
        var install = new MedalInstall(fixture.PathFor("fake-install"), secondDb, MedalFixture.AccountId, fixture.PathFor("fake-install", "Medal.exe"), Versions);
        string evidence = fixture.PathFor("enrollment-review");
        Directory.CreateDirectory(evidence);

        var clock = new SystemClock();
        var finisher = new SessionCoordinator(store, new SqliteLocalLibrary(secondDb, second.AccountId, secondState.BackupDirectory, clock),
            new Mount(), new Medal(), primary, clock, new SessionOptions(secondPc, second.Capture, Versions,
                TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(50)),
            replication: new StorageReplication(second, secondState, () => { }, primary: primary));
        int result = EnrollmentCommands.Apply(second, install, secondState, store, secondPc, review, candidate, evidence, new StringWriter(), finish: () =>
        {
            var held = store.ReadJournal();
            check(held is { State: OwnershipState.Draining, CommittedGeneration: 0 } && held.Owner == secondPc && !EnrollmentOwnership.IsPending(held)
                && store.Generations.Numbered().SequenceEqual([0L]) && !File.Exists(uploaded),
                "before its finish the enrolled PC holds the library and nothing claims its clip is on the primary");
            check(File.ReadAllText(second.MountedCapture.Append("shared.mp4").Display) == "Synthetic shared clip from the first PC."
                && File.ReadAllText(second.MountedCapture.Append("Devices", secondPc.DeviceId, "own [clip].mp4").Display) == File.ReadAllText(recording),
                "enrollment hydrates committed clips and copies its own into the persistent local copy with real rclone");
            var finished = finisher.Finish(held.SessionId!);
            return finished.Status == FinishStatus.Published ? 0 : 1;
        });
        var journal = store.ReadJournal();
        check(result == 0 && journal is { State: OwnershipState.Clean, CommittedGeneration: 1 }
            && store.Generations.Read(1).Snapshot.Digest() == candidate.Digest(),
            "the following finish publishes the union and releases ownership");
        check(File.ReadAllText(uploaded) == File.ReadAllText(recording) && File.ReadAllText(recording).Length > 0,
            "the finish uploaded and byte-verified the enrolled clip on the real named primary; the original is retained");
        check(LibraryDatabase.ReadSnapshot(secondDb, second.AccountId).Digest() == candidate.Digest(),
            "the second PC's own library is the union");

        // The first PC now sees the second PC's clip through the shared library.
        var union = store.Generations.Read(1).Snapshot;
        new StorageReplication(first, firstState, () => { }).Prepare(union);
        check(File.ReadAllText(first.MountedCapture.Append("Devices", secondPc.DeviceId, "own [clip].mp4").Display) == File.ReadAllText(recording),
            "the first PC hydrates the enrolled clip from the real named primary");
        check(EnrollmentCommands.LocalCopyConflicts(second, new RcloneAssetChecker(second, secondState), plan.Files).Count == 1,
            "a second enrollment of the same files is a conflict, because the primary already holds them");
    }

    private sealed class Mount : IClipMount
    {
        public bool IsRunning { get; private set; }
        public void Start(string session, bool forRecording) => IsRunning = true;
        public MountHealth Health() => new(true, "synthetic");
        public DrainVerdict Drain() => new(DrainState.Drained, [], 0, 0);
        public bool Stop() { IsRunning = false; return true; }
        public void Forget(string session, long generation) { }
        public void RetainAbortedStart(string session) { }
    }

    private sealed class Medal : IMedalProcess
    {
        public bool IsRunning() => false;
        public void Launch() => throw new InvalidOperationException("Finishing never starts Medal.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

using System.Runtime.Versioning;
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

/// <summary>Enrollment into a library whose primary is a named rclone remote, with a synthetic primary listing instead of rclone.</summary>
[SupportedOSPlatform("windows")]
public sealed class CloudEnrollmentTests : IDisposable
{
    private static readonly MedalVersions Versions = new("test", "test");
    private static readonly DeviceIdentity Device = new(new string('b', 32), "synthetic second PC");
    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>Primary storage as an rclone listing would report it. Uploads add files only through <see cref="Upload"/>.</summary>
    private sealed class Primary(MedalSyncConfig config) : IAssetChecker
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Offline { get; set; }
        public int Uploads { get; private set; }

        public void RequireServer()
        {
            if (Offline) throw new StorageOfflineException("Synthetic primary storage is offline.");
        }

        private AssetReport Report(LibrarySnapshot snapshot) => new(AssetInventory.Collect(snapshot)
            .Select(r => new AssetStatus(r, Files.Contains(r.Path) ? AssetProblem.None : AssetProblem.Missing, 1, DateTime.UnixEpoch, null)).ToArray());

        public AssetReport ThroughMount(LibrarySnapshot snapshot) => Report(snapshot);

        public AssetReport OnServer(LibrarySnapshot snapshot) { RequireServer(); return Report(snapshot); }

        public bool ExistsOnServer(string capturePath) { RequireServer(); return Files.Contains(capturePath); }

        /// <summary>Stands in for rclone: a copy from the local copy lands the listed files on primary storage.</summary>
        public void Upload(IReadOnlyList<string> arguments)
        {
            RequireServer();
            if (arguments[0] != "copy") return;
            Uploads++;
            string list = arguments[arguments.ToList().IndexOf("--files-from-raw") + 1];
            foreach (string relative in File.ReadAllLines(list))
                Files.Add(config.Capture.Append(relative.Split('/')).Display);
        }
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
        public void Launch() => throw new InvalidOperationException("Finish never starts Medal.");
    }

    private sealed record Setup(MedalSyncConfig Config, MedalInstall Install, LocalState State, CoordinationStore Store, Primary Primary,
        EnrollmentReview Review, LibrarySnapshot Candidate, string Database, string Evidence, string Source, string LocalTarget, string BeforeDigest)
    {
        public int Apply(string? stopAfter = null, Func<int>? finish = null) => EnrollmentCommands.Apply(Config, Install, State, Store, Device, Review,
            Candidate, Evidence, new StringWriter(), stopAfter: stopAfter, primary: Primary, finish: finish);

        public int Recover(bool rollback, bool apply, Func<int>? finish = null) => EnrollmentCommands.RecoverInterrupted(Config, Install, State, Store,
            Device, Evidence, rollback, apply, new StringWriter(), finish, Primary);

        public string Digest() => LibraryDatabase.ReadSnapshot(Database, MedalFixture.AccountId).Digest();

        public string ClipFolder()
        {
            using var db = LibraryDatabase.OpenReadOnly(Database);
            return System.Text.Json.JsonSerializer.Deserialize<string>(LibraryDatabase.ReadSetting(db, null, "ClipFolder")!)!;
        }

        /// <summary>The normal finish this PC would run, with its own uploads verified against the synthetic primary.</summary>
        public FinishOutcome Finish()
        {
            var clock = new SystemClock();
            var replication = new StorageReplication(Config, State, () => { }, Primary.Upload, primary: Primary);
            var coordinator = new SessionCoordinator(Store, new SqliteLocalLibrary(Database, MedalFixture.AccountId, Path.Combine(Evidence, "..", "backups"), clock),
                new Mount(), new Medal(), Primary, clock, new SessionOptions(Device, Config.Capture, Versions,
                    TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1)), replication: replication);
            return coordinator.Finish(Store.ReadJournal().SessionId!);
        }
    }

    /// <summary>A shared library on a cloud primary with one clip, joined by a PC with one unique local recording.</summary>
    private Setup Prepare()
    {
        string drive = Enumerable.Range('M', 14).Select(c => (char)c + @":\").First(d => !Directory.Exists(d));
        string credentials = _fixture.PathFor("synthetic.rclone.conf");
        File.WriteAllText(credentials, "[onedrive]\ntype = alias\nremote = " + _fixture.PathFor("unused") + "\n");
        var config = new MedalSyncConfig(1, new string('a', 32), MedalFixture.AccountId, _fixture.PathFor("coordination"),
            "onedrive:MedalSync", drive + "Clips", _fixture.PathFor("cache"), "unused.exe",
            new MountConfig(5, 20, 20, 300, 30, 1, 2, 53682), 4, [])
        { Replication = new(_fixture.PathFor("local-copy"), credentials, []) };
        Assert.True(config.PrimaryIsRclone);
        var primary = new Primary(config);
        string sharedClip = config.Capture.Append("shared.mp4").Display;
        primary.Files.Add(sharedClip);
        // This PC already holds the committed clip in its local copy, as after an earlier hydration.
        StorageIdentity.Initialize(config.MountedCapture.Display, config.LibraryId);
        File.WriteAllText(sharedClip.Replace(config.Capture.Display, config.MountedCapture.Display, StringComparison.Ordinal), "synthetic shared clip");
        string sharedDb = _fixture.CreateLibrary("shared");
        MedalFixture.AddContent(sharedDb, "shared", video: sharedClip);
        var shared = LibraryDatabase.ReadSnapshot(sharedDb, MedalFixture.AccountId);
        var store = CoordinationStore.Create(config.LibraryCoordinationDirectory, config.LibraryId, shared,
            new AssetManifest(1, [], []), new DeviceIdentity(new string('d', 32), "synthetic first PC"), new string('c', 32), Versions, DateTimeOffset.UtcNow);

        string database = _fixture.CreateLibrary("incoming");
        string root = _fixture.PathFor("original-capture");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "unique.mp4");
        File.WriteAllText(source, "synthetic recording");
        using (var db = MedalFixture.Open(database)) MedalFixture.SetSetting(db, "ClipFolder", System.Text.Json.JsonSerializer.Serialize(root));
        MedalFixture.AddContent(database, "unique", video: source, metadata: "{\"title\":\"Unique\"}");
        var local = LibraryDatabase.ReadSnapshot(database, MedalFixture.AccountId);
        var destination = config.Capture.Append("Devices", Device.DeviceId);
        var plan = LibraryReconciliation.Plan(shared, local, WindowsPath.Parse(root), destination);
        Assert.Empty(plan.Conflicts);
        var candidate = CountRebuilder.Rebuild(plan.Snapshot, database);
        var conflicts = EnrollmentCommands.LocalCopyConflicts(config, primary, plan.Files);
        var review = new EnrollmentReview(1, config.LibraryId, Device.DeviceId, 0, shared.Digest(), local.Digest(), candidate.Digest(),
            root, destination.Display, plan.Added, plan.Matched, plan.Identities, plan.Files, conflicts);
        var install = new MedalInstall(_fixture.PathFor("fake-install"), database, MedalFixture.AccountId, _fixture.PathFor("fake-install", "Medal.exe"), Versions);
        string evidence = _fixture.PathFor("review", "enrollment");
        Directory.CreateDirectory(evidence);
        AtomicFile.CreateNew(Path.Combine(evidence, "review.json"), Json.Serialize(review));
        AtomicFile.CreateNew(Path.Combine(evidence, "local-before.json"), local.ToCanonicalJson());
        AtomicFile.CreateNew(Path.Combine(evidence, "candidate.json"), candidate.ToCanonicalJson());
        string target = WindowsPath.Parse(Assert.Single(plan.Files).CapturePath).Rebase(config.Capture, config.MountedCapture).Display;
        return new(config, install, new LocalState(_fixture.PathFor("state")), store, primary, review, candidate, database, evidence, source, target, local.Digest());
    }

    [Fact]
    public void EnrollmentCopiesIntoTheLocalCopyAndOnlyTheFollowingFinishPublishesAndReleases()
    {
        if (!OperatingSystem.IsWindows()) return;
        var e = Prepare();
        Assert.Empty(e.Review.Conflicts);

        Assert.Equal(0, e.Apply());
        var held = e.Store.ReadJournal();
        // Handed to the ordinary finish: still this PC's, no longer marked as enrollment, nothing published.
        Assert.Equal(OwnershipState.Draining, held.State);
        Assert.Equal(Device.DeviceId, held.Owner?.DeviceId);
        Assert.False(EnrollmentOwnership.IsPending(held));
        Assert.Equal(0, held.CommittedGeneration);
        Assert.Equal([0L], e.Store.Generations.Numbered());
        Assert.Equal("synthetic recording", File.ReadAllText(e.LocalTarget));
        Assert.Equal(0, e.Primary.Uploads);
        Assert.Equal(e.Candidate.Digest(), e.Digest());
        Assert.True(WindowsPath.Parse(e.ClipFolder()).Equals(e.Config.Capture));
        Assert.Null(EnrollmentOwnership.PendingFolder(e.State.Root));
        Assert.Equal(EnrollmentCommands.HandedOff, Json.ReadFile<EnrollmentCommands.EnrollmentReceipt>(Path.Combine(e.Evidence, "apply.json"), "receipt").Stage);
        Assert.NotEmpty(DatabaseBackup.List(e.Evidence, MedalFixture.AccountId));
        Assert.Equal("synthetic recording", File.ReadAllText(e.Source));

        // Offline at that finish: the normal pending wait keeps ownership and media.
        e.Primary.Offline = true;
        var waiting = e.Finish();
        Assert.Equal(FinishStatus.Pending, waiting.Status);
        Assert.True(waiting.StorageOffline);
        Assert.Equal(held.SessionId, e.Store.ReadJournal().SessionId);
        Assert.Equal(0, e.Store.ReadJournal().CommittedGeneration);
        Assert.True(File.Exists(e.LocalTarget));

        e.Primary.Offline = false;
        var finished = e.Finish();
        Assert.Equal(FinishStatus.Published, finished.Status);
        Assert.True(e.Primary.Uploads > 0);
        Assert.Contains(Assert.Single(e.Review.Files).CapturePath, e.Primary.Files);
        var journal = e.Store.ReadJournal();
        Assert.Equal(OwnershipState.Clean, journal.State);
        Assert.Equal(1, journal.CommittedGeneration);
        Assert.Equal(e.Candidate.Digest(), e.Store.Generations.Read(1).Snapshot.Digest());
    }

    [Fact]
    public void AnOfflinePrimaryStopsApplyBeforeAnythingChanges()
    {
        if (!OperatingSystem.IsWindows()) return;
        var e = Prepare();
        e.Primary.Offline = true;
        Assert.Throws<StorageOfflineException>(() => EnrollmentCommands.LocalCopyConflicts(e.Config, e.Primary, e.Review.Files));
        var before = e.Store.ReadJournal();
        Assert.Throws<StorageOfflineException>(() => e.Apply());
        Assert.Equal(before, e.Store.ReadJournal());
        Assert.Equal(e.BeforeDigest, e.Digest());
        Assert.False(File.Exists(Path.Combine(e.Evidence, "apply.json")));
        Assert.False(File.Exists(e.LocalTarget));
        Assert.Null(EnrollmentOwnership.PendingFolder(e.State.Root));
    }

    [Fact]
    public void ConflictsAreCheckedOnThePrimaryAndInTheLocalCopy()
    {
        if (!OperatingSystem.IsWindows()) return;
        var e = Prepare();
        var file = Assert.Single(e.Review.Files);
        Directory.CreateDirectory(Path.GetDirectoryName(e.LocalTarget)!);
        File.WriteAllText(e.LocalTarget, "synthetic recording");
        Assert.Empty(EnrollmentCommands.LocalCopyConflicts(e.Config, e.Primary, e.Review.Files)); // Same bytes: a reusable copy.
        File.WriteAllText(e.LocalTarget, "different local bytes");
        Assert.Contains("different bytes", Assert.Single(EnrollmentCommands.LocalCopyConflicts(e.Config, e.Primary, e.Review.Files)), StringComparison.Ordinal);
        File.Delete(e.LocalTarget);
        e.Primary.Files.Add(file.CapturePath);
        Assert.Contains("already on primary storage", Assert.Single(EnrollmentCommands.LocalCopyConflicts(e.Config, e.Primary, e.Review.Files)), StringComparison.Ordinal);
        // Apply rechecks the primary before taking the library, and never replaces the file there.
        var before = e.Store.ReadJournal();
        Assert.Throws<UnsafeOperationException>(() => e.Apply());
        Assert.Equal(before, e.Store.ReadJournal());
        Assert.Equal(e.BeforeDigest, e.Digest());
    }

    [Fact]
    public void AnApplyKilledWhileHandingOffContinuesOrRollsBackAndAHandedOffOneOnlyRunsFinish()
    {
        if (!OperatingSystem.IsWindows()) return;
        var e = Prepare();
        Assert.Equal(-1, e.Apply(stopAfter: EnrollmentCommands.HandingOff));
        Assert.True(EnrollmentOwnership.IsPending(e.Store.ReadJournal()));
        Assert.Equal(0, e.Recover(rollback: false, apply: true));
        var handed = e.Store.ReadJournal();
        Assert.Equal(OwnershipState.Draining, handed.State);
        Assert.False(EnrollmentOwnership.IsPending(handed));
        Assert.Equal(0, handed.CommittedGeneration);

        // As if killed after the journal changed but before the receipt caught up.
        var receipt = Json.ReadFile<EnrollmentCommands.EnrollmentReceipt>(Path.Combine(e.Evidence, "apply.json"), "receipt");
        AtomicFile.Replace(Path.Combine(e.Evidence, "apply.json"), Json.Serialize(receipt with { Stage = EnrollmentCommands.HandingOff }));
        Assert.Throws<UnsafeOperationException>(() => e.Recover(rollback: true, apply: true));
        int finishes = 0;
        Assert.Equal(0, e.Recover(rollback: false, apply: false, () => ++finishes));
        Assert.Equal(0, finishes);
        Assert.Equal(7, e.Recover(rollback: false, apply: true, () => { finishes++; return 7; }));
        Assert.Equal(1, finishes);
        Assert.Equal(handed, e.Store.ReadJournal());
        Assert.Equal(e.Candidate.Digest(), e.Digest());
    }

    [Fact]
    public void ARollbackBeforeTheHandoffKeepsLocalCopiesAndGivesTheLibraryBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        var e = Prepare();
        Assert.Equal(-1, e.Apply(stopAfter: "capture-configured"));
        Assert.Equal(0, e.Recover(rollback: true, apply: true));
        var journal = e.Store.ReadJournal();
        Assert.Equal(OwnershipState.Clean, journal.State);
        Assert.Equal(0, journal.CommittedGeneration);
        Assert.Equal(e.BeforeDigest, e.Digest());
        Assert.Equal("synthetic recording", File.ReadAllText(e.LocalTarget));
        Assert.Equal(0, e.Primary.Uploads);
    }
}

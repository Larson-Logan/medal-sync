using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>Launch: acquisition, preparation and the checks before Medal starts.</summary>
public sealed partial class SessionTests
{
    [Fact]
    public void APlainLaunchWaitsWhenTheShareCannotWriteItsSwitchRequest()
    {
        string session = SessionCoordinator.NewId();
        _store.Update(r => Ownership.Acquire(r, FirstPc, session, _clock.Now), TimeSpan.FromSeconds(1));
        var owner = _store.Update(r => Ownership.MarkActive(r, FirstPc, session, _clock.Now), TimeSpan.FromSeconds(1));
        File.WriteAllText(Path.Combine(_store.Root, "requests"), "synthetic unavailable request directory");
        List<SessionProgress> progress = [];
        var pc = Computer(SecondPc, "second", progress: progress.Add);

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.Equal(LaunchWait.ServerUnreachable, outcome.Wait);
        Assert.Equal(SessionPhase.WaitingForStorage, progress[^1].Phase);
        Assert.Equal(owner, _store.ReadJournal());
        Assert.Equal(0, pc.Medal.Launches);
        Assert.Empty(pc.Mount.Starts);
    }

    [Fact]
    public void PendingPersistentMediaRefreshCannotBeAbortedByOrdinaryLaunchOrFinish()
    {
        var pc = Computer(FirstPc, "first");
        string session = SessionCoordinator.NewId();
        _store.Update(r => Ownership.Acquire(r, FirstPc, session, _clock.Now) with
        {
            Reason = MedalSync.Platform.PersistentMediaRefresh.ReasonPrefix + new string('a', 64),
        }, TimeSpan.FromSeconds(1));
        var before = _store.ReadJournal();
        Assert.Equal(LaunchStatus.Blocked, pc.Coordinator.Launch().Status);
        Assert.Equal(FinishStatus.Blocked, pc.Coordinator.Finish(session).Status);
        Assert.Equal(before, _store.ReadJournal());
        Assert.Equal(0, pc.Medal.Launches);
        Assert.Empty(pc.Mount.Starts);
        var other = Computer(SecondPc, "second");
        Assert.Equal(LaunchStatus.Blocked, other.Coordinator.Launch().Status);
        Assert.Equal(before, _store.ReadJournal());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInterruptedEnrollmentIsNeverGivenBackByLaunchOrFinish(bool caughtFailure)
    {
        // A local-only clip: giving the start back would lead to the --replace-local-library refusal.
        string db = _fixture.CreateLibrary("first");
        MedalFixture.AddContent(db, "local-only", video: @"C:\Recordings\local-only.mp4");
        string folder = _fixture.PathFor("enrollment-review");
        var pc = Computer(FirstPc, "first", database: db, enrollmentFolder: folder);
        string session = SessionCoordinator.NewId();
        // As enroll --apply takes ownership; a killed apply stays STARTING, a caught failure becomes RECOVERY_REQUIRED.
        _store.Update(r => Ownership.Acquire(r, FirstPc, session, _clock.Now) with { Reason = EnrollmentOwnership.Reason(session) }, TimeSpan.FromSeconds(1));
        if (caughtFailure)
            _store.Update(r => Ownership.RequireRecovery(r, FirstPc, session, EnrollmentOwnership.Reason(session) + " stopped", _clock.Now), TimeSpan.FromSeconds(1));
        var before = _store.ReadJournal();
        string digest = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId).Digest();

        var launched = pc.Coordinator.Launch();
        var finished = pc.Coordinator.Finish(session);

        foreach (string message in new[] { launched.Message, finished.Message })
        {
            Assert.Contains(folder, message, StringComparison.Ordinal);
            Assert.Contains("apply.json", message, StringComparison.Ordinal);
            Assert.Contains("enroll recover", message, StringComparison.Ordinal);
            Assert.Contains("Do not use 'launch --replace-local-library'", message, StringComparison.Ordinal);
        }
        Assert.Equal(LaunchStatus.Blocked, launched.Status);
        Assert.Equal(FinishStatus.Blocked, finished.Status);
        Assert.Equal(before, _store.ReadJournal());
        Assert.Empty(pc.Mount.RetainedAbortedStarts);
        Assert.Empty(pc.Mount.Starts);
        Assert.Equal(0, pc.Medal.Launches);
        Assert.Equal(digest, LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId).Digest());

        // Another PC only waits; it never takes over a half-finished enrollment.
        var other = Computer(SecondPc, "second");
        Assert.NotEqual(LaunchStatus.Started, other.Coordinator.Launch().Status);
        Assert.Equal(before.Revision, _store.ReadJournal().Revision);
    }

    [Fact]
    public void AnInterruptedEnrollmentRefusalStillExplainsWithoutARecordedFolder()
    {
        string session = SessionCoordinator.NewId();
        _store.Update(r => Ownership.Acquire(r, FirstPc, session, _clock.Now) with { Reason = EnrollmentOwnership.Reason(session) }, TimeSpan.FromSeconds(1));
        var outcome = Computer(FirstPc, "first").Coordinator.Launch();
        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("the folder you passed to 'enroll --out'", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingEnrollmentFolderIsReadOnlyForItsSession()
    {
        string root = _fixture.PathFor("state");
        string session = SessionCoordinator.NewId();
        Assert.Null(EnrollmentOwnership.PendingFolder(root));
        EnrollmentOwnership.WritePending(root, session, @"C:\Private\enroll");
        Assert.Equal(@"C:\Private\enroll", EnrollmentOwnership.PendingFolder(root));
        Assert.Equal(@"C:\Private\enroll", EnrollmentOwnership.PendingFolder(root, session));
        Assert.Null(EnrollmentOwnership.PendingFolder(root, SessionCoordinator.NewId()));
        File.WriteAllText(Path.Combine(root, "enrollment-pending.json"), "{");
        Assert.Null(EnrollmentOwnership.PendingFolder(root));
        EnrollmentOwnership.ClearPending(root);
        Assert.Null(EnrollmentOwnership.PendingFolder(root));
    }

    [Fact]
    public void IncompatibleUpdateBlocksBeforeAcquisitionRestoreOrMount()
    {
        var compatibility = new FakeCompatibility { Supported = false };
        var pc = Computer(FirstPc, "first", compatibility: compatibility);
        string before = LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).Digest();
        var journal = _store.ReadJournal();

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("incompatible Medal update", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(journal, _store.ReadJournal());
        Assert.Equal(before, LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).Digest());
        Assert.Empty(pc.Mount.Starts);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Equal(0, pc.Medal.Launches);
    }

    [Fact]
    public void StorageCheckRefusalBlocksBeforeAcquisitionRestoreOrMount()
    {
        int checks = 0;
        var pc = Computer(FirstPc, "first", preflight: () =>
        {
            checks++;
            return new LaunchPreflight(null, "Synthetic sign-in refused.");
        });
        string before = LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).Digest();
        var journal = _store.ReadJournal();

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(1, checks);
        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Equal("Synthetic sign-in refused.", outcome.Message);
        Assert.Equal(journal, _store.ReadJournal());
        Assert.Equal(before, LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).Digest());
        Assert.Empty(pc.Mount.Starts);
        Assert.Equal(0, pc.Medal.Launches);
    }

    [Fact]
    public void StorageCheckWarningStartsMedalWithANotice()
    {
        var pc = Computer(FirstPc, "first", preflight: () => new LaunchPreflight("Synthetic sign-in expired.", null),
            replication: new FakeReplication { Warning = "Synthetic primary warning." });

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Started, outcome.Status);
        Assert.Equal("Synthetic sign-in expired. Synthetic primary warning.", outcome.Warning);
        Assert.Equal(1, pc.Medal.Launches);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }

    [Fact]
    public void StorageCheckIsNotRunWhenAnotherPcOwnsTheLibrary()
    {
        var first = Computer(FirstPc, "first");
        Assert.Equal(LaunchStatus.Started, first.Coordinator.Launch().Status);
        var second = Computer(SecondPc, "second", preflight: () => throw new InvalidOperationException("Must not check while waiting."));

        Assert.Equal(LaunchStatus.Waiting, second.Coordinator.Launch().Status);
    }

    [Fact]
    public void UpdateDuringMountPreparationIsRecheckedBeforeNativeBackupOrLaunch()
    {
        var compatibility = new FakeCompatibility();
        var pc = Computer(FirstPc, "first", compatibility: compatibility,
            prepareNativeBackup: () => Assert.Fail("An incompatible build must not prepare native backups."));
        pc.Mount.OnStart = () => compatibility.Supported = false;

        Assert.Equal(LaunchStatus.Blocked, pc.Coordinator.Launch().Status);

        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.False(pc.Medal.Running);
        Assert.False(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void UpdateDuringNativeBackupPreparationAlsoBlocksMedal()
    {
        var compatibility = new FakeCompatibility();
        var pc = Computer(FirstPc, "first", compatibility: compatibility,
            prepareNativeBackup: () => compatibility.Supported = false);

        Assert.Equal(LaunchStatus.Blocked, pc.Coordinator.Launch().Status);

        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(0, pc.Medal.Launches);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void NativeBackupPreparationRunsAfterVerifiedRestoreAndMountBeforeMedalStarts()
    {
        Pc? pc = null;
        bool prepared = false;
        pc = Computer(SecondPc, "second", prepareNativeBackup: () =>
        {
            Assert.NotNull(pc);
            Assert.True(pc.Mount.IsRunning);
            Assert.False(pc.Medal.Running);
            Assert.Equal(OwnershipState.Starting, _store.ReadJournal().State);
            Assert.Equal(_store.Generations.Read(0).Snapshot.Digest(), LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).Digest());
            prepared = true;
        });
        Assert.Equal(LaunchStatus.Started, pc.Coordinator.Launch().Status);
        Assert.True(prepared);
        Assert.True(pc.Medal.Running);
    }

    [Fact]
    public void UnsafeNativeBackupBlocksMedalAndReturnsTheUntouchedGeneration()
    {
        var pc = Computer(SecondPc, "second", prepareNativeBackup: () => throw new UnsafeOperationException("incomplete native backup"));
        Assert.Equal(LaunchStatus.Blocked, pc.Coordinator.Launch().Status);
        Assert.False(pc.Medal.Running);
        Assert.False(pc.Mount.IsRunning);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
    }

    [Fact]
    public void Handoff_CarriesClipsBothWaysAndKeepsDeviceSettings()
    {
        var first = Computer(FirstPc, "first");
        var second = Computer(SecondPc, "second");

        AssertStarted(first.Coordinator.Launch());
        string firstSession = _store.ReadJournal().SessionId!;
        Assert.Equal(1, LibraryDatabase.ReadSnapshot(first.Database, MedalFixture.AccountId).RowCount("contents"));
        Record(first, "clip-2");

        // The second PC asks while the first is busy, without taking anything.
        var waiting = second.Coordinator.Launch();
        Assert.Equal(LaunchStatus.Waiting, waiting.Status);
        Assert.Single(_store.PendingRequests(_clock.Now));
        Assert.Empty(second.Mount.Starts);

        first.Medal.Running = false;
        var published = first.Coordinator.Finish(firstSession);
        Assert.Equal(FinishStatus.Published, published.Status);
        Assert.Equal(1, published.Generation);
        Assert.False(first.Mount.IsRunning);

        AssertStarted(second.Coordinator.Launch());
        Assert.Empty(_store.PendingRequests(_clock.Now));
        var onSecond = LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId);
        Assert.Equal(2, onSecond.RowCount("contents"));
        Assert.Contains("second-microphone", MedalFixture.Settings(second.Database), StringComparison.Ordinal);
        Record(second, "clip-3");
        second.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, second.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);

        AssertStarted(first.Coordinator.Launch());
        Assert.Equal(3, LibraryDatabase.ReadSnapshot(first.Database, MedalFixture.AccountId).RowCount("contents"));
        Assert.Contains("first-microphone", MedalFixture.Settings(first.Database), StringComparison.Ordinal);
        Assert.Equal(2, _store.ReadJournal().CommittedGeneration);
    }

    [Fact]
    public void Launch_RefusesAnUnmanagedMedal()
    {
        var pc = Computer(FirstPc, "first");
        pc.Medal.Running = true;

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Starts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Launch_RefusesToAdoptRunningMedalWithAnUnverifiedDrive(bool mounted, bool retryable)
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        var before = _store.ReadJournal();
        Record(pc, "unpublished-clip");
        string digest = LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).Digest();
        if (!mounted) pc.Mount.Stop();
        pc.Mount.HealthResult = new MountHealth(false, "drive identity unavailable", Retryable: retryable);

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("Quit Medal from its tray", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(before, _store.ReadJournal());
        Assert.Equal(digest, LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).Digest());
        Assert.Single(pc.Mount.Starts);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Equal(1, pc.Medal.Launches);
    }

    [Fact]
    public void Launch_AdoptsRunningMedalOnlyWithAVerifiedDrive()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        var before = _store.ReadJournal();

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.AlreadyRunning, outcome.Status);
        Assert.Equal(before.SessionId, outcome.Session);
        Assert.Equal(before, _store.ReadJournal());
        Assert.Single(pc.Mount.Starts);
        Assert.Equal(1, pc.Medal.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Launch_ReturnsOwnershipWhenStartingFails(bool retryable)
    {
        var pc = Computer(FirstPc, "first");
        pc.Mount.HealthResult = new MountHealth(false, "the library marker is missing on M:", Retryable: retryable);

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("marker", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.False(pc.Mount.IsRunning);
        Assert.Equal(0, pc.Medal.Launches);
    }

    [Fact]
    public void StoppedFailedStartRetainsUnknownCacheAndLoginWithoutBlockingSetup()
    {
        var (state, config) = RecoverySetup();
        var pc = Computer(FirstPc, "first");
        pc.Mount.HealthResult = new(false, "Synthetic failed health check");
        pc.Mount.OnStarted = session =>
        {
            _ = state.SessionSecret(session, _clock.Now);
            Directory.CreateDirectory(config.SessionCacheDirectory(session));
            File.WriteAllText(Path.Combine(config.SessionCacheDirectory(session), "unknown-cache"), "retained private bytes");
        };
        pc.Mount.OnRetainAbortedStart = session =>
        {
            Assert.Equal(OwnershipState.Starting, _store.ReadJournal().State);
            state.RetainAbortedStart(session, LibraryId, config.SessionCacheDirectory(session), _clock.Now);
        };
        Assert.Equal(LaunchStatus.Blocked, pc.Coordinator.Launch().Status);
        string session = Assert.Single(pc.Mount.RetainedAbortedStarts);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal("retained private bytes", File.ReadAllText(Path.Combine(config.SessionCacheDirectory(session), "unknown-cache")));
        Assert.True(File.Exists(Path.Combine(state.Root, "aborted-starts", session, "login.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(state.Root, "sessions")));
        Assert.Empty(pc.Mount.Forgotten);
        SetupMaintenance.RequireReady(state.Root, false, false, _ => _store.ReadJournal());
    }

    [Fact]
    public void EvidenceFailureOrRunningMedalRefusesExplicitStartingRecovery()
    {
        var pc = Computer(FirstPc, "first");
        pc.Mount.HealthResult = new(false, "Synthetic failed health check");
        pc.Mount.OnRetainAbortedStart = _ => throw new IOException("Evidence storage unavailable");
        Assert.Equal(LaunchStatus.Blocked, pc.Coordinator.Launch().Status);
        string session = _store.ReadJournal().SessionId!;
        Assert.Equal(OwnershipState.Starting, _store.ReadJournal().State);
        pc.Mount.OnRetainAbortedStart = null;
        pc.Medal.Running = true;
        Assert.Equal(FinishStatus.Blocked, pc.Coordinator.Finish(session).Status);
        Assert.Equal(OwnershipState.Starting, _store.ReadJournal().State);
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Unchanged, pc.Coordinator.Finish(session).Status);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void FailedStartupRetainsOwnershipUntilMountCleanupIsVerified(bool stopResult, bool stillRunning, bool stopThrows)
    {
        var pc = Computer(FirstPc, "first");
        pc.Mount.HealthResult = new MountHealth(false, "Synthetic unhealthy mount.");
        pc.Mount.StopResult = stopResult;
        pc.Mount.RemainRunningOnStop = stillRunning;
        pc.Mount.StopThrows = stopThrows;

        var blocked = pc.Coordinator.Launch();
        var retained = _store.ReadJournal();

        Assert.Equal(LaunchStatus.Blocked, blocked.Status);
        Assert.Contains("retry", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OwnershipState.Starting, retained.State);
        Assert.Equal(FirstPc, retained.Owner);
        Assert.Equal(pc.Mount.Starts.Single(), retained.SessionId);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Empty(pc.Mount.RetainedAbortedStarts);
        Assert.Equal(0, pc.Medal.Launches);
        var other = Computer(SecondPc, "second");
        Assert.Equal(LaunchStatus.Waiting, other.Coordinator.Launch().Status);
        Assert.Equal(retained, _store.ReadJournal());

        // Recover the same STARTING journal, as after a companion restart.
        Assert.Equal(LaunchStatus.Blocked, pc.Coordinator.Launch().Status);
        Assert.Equal(retained, _store.ReadJournal());
        pc.Mount.StopResult = true;
        pc.Mount.RemainRunningOnStop = false;
        pc.Mount.StopThrows = false;
        pc.Mount.HealthResult = new MountHealth(true, "ok");
        AssertStarted(pc.Coordinator.Launch());
        Assert.NotEqual(retained.SessionId, _store.ReadJournal().SessionId);
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Unchanged, pc.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        AssertStarted(other.Coordinator.Launch());
    }

    [Fact]
    public void Launch_NeverStartsMedalWhenFilesAreMissingBehindTheMount()
    {
        var pc = Computer(FirstPc, "first");
        _assets.MountBroken = true;

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Equal(0, pc.Medal.Launches);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void Launch_RequiresTheCommonCaptureFolder()
    {
        var pc = Computer(FirstPc, "first");
        using (var connection = MedalFixture.Open(pc.Database))
        {
            MedalFixture.SetSetting(connection, "ClipFolder", "\"D:\\\\Clips\\\\\"");
        }

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains(@"M:\Clips", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(pc.Mount.Starts);
    }

    [Fact]
    public void Launch_FinishesThisPcsInterruptedSessionFirst()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        string interrupted = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        // Power loss: Medal and the mount are gone, the journal still says ACTIVE.
        pc.Medal.Running = false;
        pc.Mount.Stop();

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Started, outcome.Status);
        Assert.NotEqual(interrupted, outcome.Session);
        Assert.Equal(interrupted, pc.Mount.Starts[1]);
        Assert.Equal([true, false, true], pc.Mount.ForRecording);
        Assert.Equal(1, _store.ReadJournal().CommittedGeneration);
        Assert.Contains(interrupted, pc.Mount.Forgotten);
    }

    [Fact]
    public void Launch_IsBlockedWhileAnotherPcNeedsRecovery()
    {
        var first = Computer(FirstPc, "first");
        first.Coordinator.Launch();
        string session = _store.ReadJournal().SessionId!;
        _store.Update(r => Ownership.RequireRecovery(r, FirstPc, session, "network lost while recording", _clock.Now), TimeSpan.FromSeconds(1));
        var second = Computer(SecondPc, "second");

        var outcome = second.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("network lost", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(FirstPc, _store.ReadJournal().Owner);
    }

    [Fact]
    public void Launch_RechecksMedalBeforeRestoring()
    {
        var pc = Computer(FirstPc, "first");
        string before = LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).Digest();
        var medal = new MedalOpenedLater();
        var coordinator = new SessionCoordinator(_store,
            new SqliteLocalLibrary(pc.Database, MedalFixture.AccountId, _fixture.PathFor("first", "backups"), _clock),
            pc.Mount, medal, _assets, _clock,
            new SessionOptions(FirstPc, WindowsPath.Parse(@"M:\Clips"), Versions, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10)));

        var outcome = coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("opened while the shared library was being prepared", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(before, LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).Digest());
        Assert.Empty(pc.Mount.Starts);
    }

    [Fact]
    public void Launch_WaitsInsteadOfTrustingAStaleJournal()
    {
        // Windows served the other PC's replaced journal from a cached open; the listing showed the newer file.
        var other = Computer(SecondPc, "second");
        Assert.Equal(LaunchStatus.Started, other.Coordinator.Launch().Status);
        var pc = Computer(FirstPc, "first");
        var listed = new DateTime(2026, 11, 9, 0, 32, 21, DateTimeKind.Utc);
        var defaultReader = _store.JournalReader;
        _store.JournalReader = new FreshCoordinationRead
        {
            ReadOpen = _ => ("{}"u8.ToArray(), listed.AddMinutes(-1)),
            ReadListing = _ => listed,
            Sleep = _ => { },
            PauseDirectory = _fixture.PathFor("pauses"),
        };

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.True(outcome.StorageOffline);
        Assert.Equal(LaunchWait.ServerUnreachable, outcome.Wait);
        Assert.Contains("out-of-date copy of the library's ownership journal", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, pc.Medal.Launches);
        Assert.Empty(pc.Mount.Starts);
        _store.JournalReader = defaultReader;
        Assert.Equal(SecondPc.DeviceId, _store.ReadJournal().Owner?.DeviceId);
    }

    [Fact]
    public void Launch_RefusesToOverwriteChangesThatWereNeverShared()
    {
        string db = _fixture.CreateLibrary("second");
        MedalFixture.AddContent(db, "recorded-without-medal-sync", video: @"M:\Clips\Clips\offline.mp4");
        string before = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId).Digest();
        var pc = Computer(SecondPc, "second", database: db);

        var outcome = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("--replace-local-library", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(before, LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId).Digest());
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);

        var replacing = Computer(SecondPc, "second", replaceLocal: true, database: db);
        var started = replacing.Coordinator.Launch();
        AssertStarted(started);
        Assert.Contains("backed up to", started.Message, StringComparison.Ordinal);
        Assert.Contains(SessionCoordinator.KeptBackupNote, started.Message, StringComparison.Ordinal);
        // The backup holds the only copy of the unshared entry, so it is kept apart from the pruned backups.
        string kept = Assert.Single(Directory.EnumerateFiles(_fixture.PathFor("second", "backups"), "*.db", SearchOption.AllDirectories));
        Assert.Contains(kept, started.Message, StringComparison.Ordinal);
        Assert.StartsWith(DatabaseBackup.KeptPrefix, Path.GetFileName(Path.GetDirectoryName(kept)), StringComparison.Ordinal);
        Assert.Empty(DatabaseBackup.List(_fixture.PathFor("second", "backups"), MedalFixture.AccountId));
        Assert.Contains(LibraryDatabase.ReadSnapshot(kept, MedalFixture.AccountId).Table("contents").Rows,
            r => r.Any(c => c is TextCell { Value: "recorded-without-medal-sync" }));
    }

    [Fact]
    public void Launch_GivesTheSessionBackAfterAnUnexpectedFailure()
    {
        var pc = Computer(FirstPc, "first");
        pc.Mount.OnStart = () => throw new InvalidOperationException("antivirus blocked rclone");

        Assert.Throws<InvalidOperationException>(() => pc.Coordinator.Launch());

        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(0, pc.Medal.Launches);
    }

    [Fact]
    public void HydrationFailureReturnsOwnershipBeforeMedalLaunch()
    {
        var replication = new FakeReplication { OnPrepare = () => throw new IOException("conflicting local file") };
        var pc = Computer(FirstPc, "first", replication: replication);
        Assert.Equal(LaunchStatus.Blocked, pc.Coordinator.Launch().Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Starts);
        Assert.Equal(0, pc.Medal.Launches);
    }

    [Fact]
    public void OfflineHydrationAtLaunchWaitsInsteadOfBlocking()
    {
        List<SessionProgress> reports = [];
        var replication = new FakeReplication { OnPrepare = () => throw new StorageOfflineException("synthetic outage") };
        var pc = Computer(FirstPc, "first", replication: replication, progress: reports.Add);
        var outcome = pc.Coordinator.Launch();
        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.True(outcome.StorageOffline);
        Assert.Equal(SessionPhase.WaitingForStorage, reports.Last().Phase);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(0, pc.Medal.Launches);
    }

    [Fact]
    public void UnreachableJournalAtLaunchIsAWaitNotAnException()
    {
        var pc = Computer(FirstPc, "first");
        string offline = _store.Root + "-offline";
        Directory.Move(_store.Root, offline);
        try
        {
            var outcome = pc.Coordinator.Launch();
            Assert.Equal(LaunchStatus.Waiting, outcome.Status);
            Assert.True(outcome.StorageOffline);
            Assert.Equal(0, pc.Medal.Launches);
        }
        finally { Directory.Move(offline, _store.Root); }
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void ReopeningMedalWhileTheLastFinishNeedsActionNamesTheCauseInsteadOfWaiting()
    {
        var replication = new FakeReplication { OnPublish = () => throw new StorageActionRequiredException("Primary storage onedrive:MedalSync did not accept rclone's sign-in.") };
        List<SessionProgress> reports = [];
        var pc = Computer(FirstPc, "first", replication: replication, progress: reports.Add);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        Assert.Equal(PendingCause.NeedsAction, pc.Coordinator.Finish(session).Cause);

        var relaunch = pc.Coordinator.Launch();

        Assert.Equal(LaunchStatus.Blocked, relaunch.Status);
        Assert.Contains("Action needed before switching PCs: Primary storage onedrive:MedalSync did not accept", relaunch.Message, StringComparison.Ordinal);
        Assert.Equal(SessionPhase.RecoveryNeeded, reports[^1].Phase);
        Assert.Equal(1, pc.Medal.Launches);
        Assert.Equal(session, _store.ReadJournal().SessionId);
    }

    [Fact]
    public void LaunchStartsMedalButWarnsWhenPrimaryStorageNeedsTheUser()
    {
        const string warning = "Medal started, but the switch to another PC cannot finish until this is fixed: synthetic sign-in problem";
        List<SessionProgress> reports = [];
        var pc = Computer(FirstPc, "first", replication: new FakeReplication { Warning = warning }, progress: reports.Add);

        var outcome = pc.Coordinator.Launch();

        AssertStarted(outcome);
        Assert.Equal(warning, outcome.Warning);
        Assert.Contains(warning, outcome.Message, StringComparison.Ordinal);
        Assert.Equal(1, pc.Medal.Launches);
        Assert.Contains(warning, reports[^1].NextAction, StringComparison.Ordinal);
    }
}

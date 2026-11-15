using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>Supervision while Medal runs, Windows shutdown and upload retries.</summary>
public sealed partial class SessionTests
{
    [Fact]
    public void StatusReadyIsEmittedOnlyAfterVerifiedPublicationAndRelease()
    {
        List<SessionProgress> reports = [];
        List<OwnershipState> readyStates = [];
        var pc = Computer(FirstPc, "first", progress: report =>
        {
            reports.Add(report);
            if (report.Phase == SessionPhase.Ready) readyStates.Add(_store.ReadJournal().State);
        });
        AssertStarted(pc.Coordinator.Launch());
        Record(pc, "new-recording");
        pc.Medal.Running = false;
        string session = _store.ReadJournal().SessionId!;
        pc.Mount.Verdicts.Enqueue(new(DrainState.Pending, [], 2, 8192));
        pc.Mount.Verdicts.Enqueue(new(DrainState.Drained, [], 0, 0));
        _assets.OnServerCheck = () => Assert.DoesNotContain(reports, p => p.Phase == SessionPhase.Ready);
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Contains(reports, p => p.Phase == SessionPhase.Uploading && p.QueuedFiles == 2 && p.QueuedBytes == 8192);
        Assert.Contains(reports, p => p.Phase == SessionPhase.Verifying);
        Assert.Equal(SessionPhase.Ready, reports[^1].Phase);
        Assert.Equal(new[] { OwnershipState.Clean }, readyStates);
    }

    [Fact]
    public void UnknownUploadsAndLateFailuresNeverEmitReadyAndCanRecover()
    {
        List<SessionProgress> reports = [];
        var pc = Computer(FirstPc, "first", progress: reports.Add);
        AssertStarted(pc.Coordinator.Launch());
        pc.Medal.Running = false;
        string session = _store.ReadJournal().SessionId!;
        pc.Mount.Steady = DrainVerdict.Unknown("fake disconnected supervisor");
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Finish(session).Status);
        Assert.Contains(reports, p => p.Phase == SessionPhase.Unknown && p.QueuedBytes is null);
        Assert.DoesNotContain(reports, p => p.Phase == SessionPhase.Ready);
        pc.Mount.Steady = new(DrainState.Drained, [], 0, 0);
        pc.Mount.OnStop = () => Record(pc, "late-recording");
        Assert.Equal(FinishStatus.Blocked, pc.Coordinator.Finish(session).Status);
        Assert.Equal(SessionPhase.RecoveryNeeded, reports[^1].Phase);
        Assert.DoesNotContain(reports, p => p.Phase == SessionPhase.Ready);
        Assert.Empty(pc.Mount.Forgotten);
        pc.Mount.OnStop = null;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(SessionPhase.Ready, reports[^1].Phase);
    }

    [Fact]
    public void ViewDisconnectCannotInterruptTheSupervisor()
    {
        var pc = Computer(FirstPc, "first", progress: _ => throw new IOException("view disconnected"));
        AssertStarted(pc.Coordinator.Launch());
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Unchanged, pc.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupervisionChecksUpdatesOnceAMinuteAndWarnsOnceWithoutStoppingMedal(bool checkFails)
    {
        var compatibility = new FakeCompatibility { Warning = "Synthetic Medal update detected", CheckFails = checkFails };
        var pc = Computer(FirstPc, "first", compatibility: compatibility);
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        using var cancel = new CancellationTokenSource();
        int polls = 0;
        _clock.OnSleep = () => { if (++polls == 14) cancel.Cancel(); };
        var messages = new List<string>();

        var result = pc.Coordinator.Supervise(session, messages.Add, cancel.Token);

        Assert.Equal(FinishStatus.Pending, result.Status);
        Assert.Equal(3, compatibility.Checks);
        Assert.Single(messages);
        Assert.True(pc.Medal.Running);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }

    [Fact]
    public void SupervisionReportsRecordingOnlyWhileMedalRecordsAGame()
    {
        var reports = new List<SessionProgress>();
        var pc = Computer(FirstPc, "first", progress: reports.Add);
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        int polls = 0;
        // Idle, then a game, then a failing check: a check that cannot run never claims recording.
        pc.Medal.Recording = () => polls switch
        {
            < 2 => false,
            < 4 => true,
            _ => throw new IOException("clip drive is slow"),
        };
        using var cancel = new CancellationTokenSource();
        _clock.OnSleep = () => { if (++polls == 6) cancel.Cancel(); };

        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Supervise(session, _ => { }, cancel.Token).Status);

        var active = reports.Where(r => r.Phase == SessionPhase.Active).ToList();
        Assert.False(active[0].Recording);
        Assert.Contains("Medal is open", active[0].NextAction, StringComparison.Ordinal);
        Assert.Contains(active, r => r.Recording && r.NextAction.Contains("recording a game", StringComparison.Ordinal));
        Assert.False(active[^1].Recording);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }

    [Fact]
    public void SupervisionWarnsAboutLowDiskSpaceWithoutStoppingMedalOrTheMount()
    {
        long free = 100L << 30;
        var space = new SpacePressureMonitor([new("SSD cache", @"E:\MedalCache")], 40L << 30, 20L << 30,
            _ => new VolumeInfo(@"E:\", "volume-e", VolumeKind.Fixed, "NTFS", free -= 30L << 30, 500L << 30));
        var pc = Computer(FirstPc, "first", space: space);
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        using var cancel = new CancellationTokenSource();
        int polls = 0;
        _clock.OnSleep = () => { if (++polls == 12) cancel.Cancel(); };
        var messages = new List<string>();

        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Supervise(session, messages.Add, cancel.Token).Status);

        // 70 GB, then 40 GB (low), then 10 GB (at the reserve): one notice for each change, none for the first check.
        Assert.Equal(2, messages.Count);
        Assert.StartsWith("Low disk space on E:\\ (SSD cache)", messages[0], StringComparison.Ordinal);
        Assert.Contains("reserve", messages[1], StringComparison.Ordinal);
        Assert.True(pc.Medal.Running);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }

    [Fact]
    public void FailedWindowsStartRetainsSessionAndRetryRecoversIt()
    {
        var pc = Computer(FirstPc, "first");
        pc.Medal.OnLaunch = () => throw new UnsafeOperationException("Synthetic Windows start failure.");
        Assert.Throws<UnsafeOperationException>(() => pc.Coordinator.Launch());
        var retained = _store.ReadJournal();
        Assert.Equal(OwnershipState.Active, retained.State);
        Assert.Equal(FirstPc, retained.Owner);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.False(pc.Medal.Running);

        pc.Medal.OnLaunch = null;
        var retried = pc.Coordinator.Launch();
        Assert.Equal(LaunchStatus.Started, retried.Status);
        Assert.NotEqual(retained.SessionId, retried.Session);
        Assert.Contains(retained.SessionId!, pc.Mount.Forgotten);
        Assert.True(pc.Medal.Running);
    }

    [Fact]
    public void ShutdownBeforeLaunchDoesNotAcquireOrStartAnything()
    {
        var shutdown = new SessionShutdown(_clock);
        shutdown.RequestEnd();
        var pc = Computer(FirstPc, "first", shutdown: shutdown);
        Assert.Equal(LaunchStatus.Waiting, pc.Coordinator.Launch().Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Starts);
        Assert.Equal(0, pc.Medal.Launches);
    }

    [Fact]
    public void ShutdownDuringPreparationGivesBackStartWithoutOpeningMedal()
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown, prepareNativeBackup: shutdown.RequestEnd);
        Assert.Equal(LaunchStatus.Blocked, pc.Coordinator.Launch().Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.False(pc.Mount.IsRunning);
        Assert.Equal(0, pc.Medal.Launches);
    }

    [Fact]
    public void ShutdownWaitsForRecorderExitBeforeReadingAndPublishingFinalClip()
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown);
        string session = pc.Coordinator.Launch().Session!;
        _clock.OnSleep = () =>
        {
            // Model Windows asking Medal to save/exit, including its encoder.
            _clock.OnSleep = null;
            MedalFixture.AddContent(pc.Database, "last-recording", video: @"M:\Clips\last.mp4");
            _assets.OnServerFiles.Add(@"M:\Clips\last.mp4");
            pc.Medal.Running = false;
        };
        shutdown.RequestEnd();
        var result = pc.Coordinator.Supervise(session, _ => Assert.Fail("No shutdown prompts"), CancellationToken.None);
        Assert.Equal(FinishStatus.Published, result.Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(2, _store.Generations.Read(1).Snapshot.RowCount("contents"));
        Assert.Contains(session, pc.Mount.Forgotten);
    }

    [Fact]
    public void ShutdownWithRecorderStillRunningTimesOutWithoutTouchingCacheOrJournal()
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown);
        string session = pc.Coordinator.Launch().Session!;
        shutdown.RequestEnd();
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Supervise(session, _ => Assert.Fail(), CancellationToken.None).Status);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShutdownTimeoutKeepsUnsentRecordingAndNextStartupRecoversSameSession(bool unknown)
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown);
        string session = pc.Coordinator.Launch().Session!;
        MedalFixture.AddContent(pc.Database, "new-clip", video: @"M:\Clips\new.mp4");
        pc.Medal.Running = false;
        pc.Mount.Steady = unknown ? DrainVerdict.Unknown("status unavailable")
            : new DrainVerdict(DrainState.Pending, ["still uploading"], 1, 0);
        shutdown.RequestEnd();
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Supervise(session, _ => Assert.Fail(), CancellationToken.None).Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(session, _store.ReadJournal().SessionId);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Equal(2, LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId).RowCount("contents"));
        _assets.OnServerFiles.Add(@"M:\Clips\new.mp4");
        var rebooted = Computer(FirstPc, "rebooted", database: pc.Database);
        Assert.Equal(LaunchStatus.Started, rebooted.Coordinator.Launch().Status);
        Assert.Equal(session, rebooted.Mount.Starts[0]);
        Assert.False(rebooted.Mount.ForRecording[0]);
        Assert.Equal(1, _store.ReadJournal().CommittedGeneration);
        Assert.Equal(2, _store.Generations.Read(1).Snapshot.RowCount("contents"));
    }

    [Fact]
    public void ShutdownResumesCacheIfWindowsAlreadyStoppedUploader()
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        pc.Mount.Stop();
        shutdown.RequestEnd();
        Assert.Equal(FinishStatus.Unchanged, pc.Coordinator.Supervise(session, _ => Assert.Fail(), CancellationToken.None).Status);
        Assert.Equal(new[] { session, session }, pc.Mount.Starts);
        Assert.Equal(new[] { true, false }, pc.Mount.ForRecording);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void ShutdownOfflineKeepsOwnershipAndCache()
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        _assets.ServerReachable = false;
        shutdown.RequestEnd();
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Supervise(session, _ => Assert.Fail(), CancellationToken.None).Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void ShutdownNeverPromptsOrAcceptsSuspectRemovals()
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown, reviewLibraryChanges: _ => { Assert.Fail("Review deferred until sign-in"); return true; });
        string session = pc.Coordinator.Launch().Session!;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id = 'clip-1'");
        pc.Medal.Running = false;
        shutdown.RequestEnd();
        Assert.Equal(FinishStatus.Blocked, pc.Coordinator.Supervise(session, _ => Assert.Fail(), CancellationToken.None).Status);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void ShutdownPreservesExplicitDeletionRecordsWithoutRequiringReview()
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown, reviewLibraryChanges: _ => { Assert.Fail(); return false; });
        string session = pc.Coordinator.Launch().Session!;
        MedalFixture.DeleteContent(pc.Database, "clip-1");
        pc.Medal.Running = false;
        shutdown.RequestEnd();
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Supervise(session, _ => Assert.Fail(), CancellationToken.None).Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(0, _store.Generations.Read(1).Snapshot.RowCount("contents"));
        Assert.Equal(1, _store.Generations.Read(1).Snapshot.RowCount("deleted_contents"));
    }

    [Fact]
    public void ShutdownDoesNotPublishMissingServerAssets()
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown);
        string session = pc.Coordinator.Launch().Session!;
        MedalFixture.AddContent(pc.Database, "not-uploaded", video: @"M:\Clips\missing.mp4");
        pc.Medal.Running = false;
        shutdown.RequestEnd();
        Assert.Equal(FinishStatus.Blocked, pc.Coordinator.Supervise(session, _ => Assert.Fail(), CancellationToken.None).Status);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void BudgetExpiringDuringServerAuditCannotCommitOrRemoveCache()
    {
        var shutdown = new SessionShutdown(_clock);
        var pc = Computer(FirstPc, "first", shutdown: shutdown);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        _assets.OnServerCheck = () => _clock.Sleep(TimeSpan.FromSeconds(30));
        shutdown.RequestEnd();
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Supervise(session, _ => Assert.Fail(), CancellationToken.None).Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);
    }

    private static DrainVerdict RetryingUpload(long tries = 3, ulong id = 1) => VfsStatus.Evaluate(
        """{"fs":"synthetic:media","diskCache":{"uploadsInProgress":0,"uploadsQueued":1,"erroredFiles":0,"outOfSpace":false}}""",
        $$"""{"queue":[{"id":{{id}},"size":10,"tries":{{tries}},"uploading":false}]}""", "synthetic:media");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupervisionRetriesTransientCacheCleanupAutomatically(bool outOfSpace)
    {
        List<SessionProgress> reports = [];
        var pc = Computer(FirstPc, "first", progress: reports.Add);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        string cache = outOfSpace ? "\"erroredFiles\":0,\"outOfSpace\":true" : "\"erroredFiles\":2,\"outOfSpace\":false";
        var verdict = VfsStatus.Evaluate("{\"fs\":\"synthetic:media\",\"diskCache\":{\"uploadsInProgress\":0,\"uploadsQueued\":0," + cache + "}}",
            "{\"queue\":[]}", "synthetic:media");
        pc.Mount.Verdicts.Enqueue(verdict);

        Assert.Equal(FinishStatus.Unchanged, pc.Coordinator.Supervise(session, _ => { }, TestContext.Current.CancellationToken).Status);
        Assert.Contains(reports, r => r.NextAction.Contains("SSD cache", StringComparison.Ordinal));
        Assert.DoesNotContain(reports, r => r.NextAction.Contains("failed to upload", StringComparison.Ordinal));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void RepeatedUploadFailuresBecomeActionNeededAndRecoverWithBackoff()
    {
        List<SessionProgress> reports = [];
        List<string> notices = [];
        var pc = Computer(FirstPc, "first", progress: reports.Add);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        pc.Mount.Steady = RetryingUpload();
        var first = pc.Coordinator.Finish(session);
        Assert.Equal(PendingCause.None, first.Cause);
        Assert.Contains("being retried", first.Message);
        _clock.Sleep(TimeSpan.FromMinutes(15));
        var action = pc.Coordinator.Finish(session);
        Assert.Equal(FinishStatus.Pending, action.Status);
        Assert.Equal(PendingCause.NeedsAction, action.Cause);
        Assert.Contains("Action needed", action.Message);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Contains(reports, r => r.Phase == SessionPhase.RecoveryNeeded && r.NextAction.Contains("being retried", StringComparison.Ordinal));

        var resumed = _clock.Now;
        var attempts = new List<DateTimeOffset>();
        pc.Mount.OnDrain = () =>
        {
            attempts.Add(_clock.Now);
            if (_clock.Now - resumed >= TimeSpan.FromMinutes(2)) pc.Mount.Steady = new(DrainState.Drained, [], 0, 0);
        };
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Supervise(session, notices.Add, TestContext.Current.CancellationToken).Status);
        Assert.Single(notices, n => n.StartsWith("Action needed", StringComparison.Ordinal));
        Assert.True(attempts[1] - attempts[0] >= TimeSpan.FromSeconds(30));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UploadRetryAgeDoesNotFollowNewOrResetQueueEntries(bool newId)
    {
        var pc = Computer(FirstPc, "first");
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        pc.Mount.Steady = RetryingUpload();
        pc.Coordinator.Finish(session);
        _clock.Sleep(TimeSpan.FromMinutes(15));
        pc.Mount.Steady = newId ? RetryingUpload(id: 2) : RetryingUpload(tries: 0);
        Assert.Equal(PendingCause.None, pc.Coordinator.Finish(session).Cause);
        pc.Mount.Steady = new(DrainState.Drained, [], 0, 0);
        Assert.Equal(FinishStatus.Unchanged, pc.Coordinator.Finish(session).Status);
    }

    [Theory]
    [InlineData("UHUH", 0)]
    [InlineData("UUHUUH", 0)]
    [InlineData("UUUU", 1)]
    [InlineData("X", 1)]
    public void Supervise_ConfirmsStatusTimeoutsButReportsDefiniteFailuresImmediately(string observations, int warnings)
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        foreach (char observation in observations)
        {
            pc.Mount.HealthResults.Enqueue(observation switch
            {
                'H' => new MountHealth(true, "ok"),
                'U' => new MountHealth(false, "status timeout", Retryable: true),
                _ => new MountHealth(false, "wrong library marker"),
            });
        }
        using var cancellation = new CancellationTokenSource();
        int polls = 0;
        var clock = new StoppingClock(_clock, () =>
        {
            if (++polls == observations.Length) cancellation.Cancel();
        });
        var coordinator = new SessionCoordinator(_store,
            new SqliteLocalLibrary(pc.Database, MedalFixture.AccountId, _fixture.PathFor("first", "backups"), clock),
            pc.Mount, pc.Medal, _assets, clock,
            new SessionOptions(FirstPc, WindowsPath.Parse(@"M:\Clips"), Versions, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10)));
        var messages = new List<string>();

        coordinator.Supervise(session, messages.Add, cancellation.Token);

        Assert.Equal(warnings, messages.Count(m => m.StartsWith("Clip drive problem:", StringComparison.Ordinal)));
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
        Assert.True(pc.Mount.IsRunning);
    }

    [Fact]
    public void Supervise_FinishesWhenMedalExits()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        string session = _store.ReadJournal().SessionId!;
        var messages = new List<string>();
        _store.WriteRequest(new HandoffRequest(1, SecondPc, SessionCoordinator.NewId(), _clock.Now));
        pc.Mount.HealthResult = new MountHealth(true, "ok");
        int polls = 0;
        var medal = pc.Medal;
        using var cancel = new CancellationTokenSource();
        var clock = new StoppingClock(_clock, () =>
        {
            if (++polls == 3) medal.Running = false;
        });
        var coordinator = new SessionCoordinator(_store,
            new SqliteLocalLibrary(pc.Database, MedalFixture.AccountId, _fixture.PathFor("first", "backups"), _clock),
            pc.Mount, medal, _assets, clock,
            new SessionOptions(FirstPc, WindowsPath.Parse(@"M:\Clips"), Versions, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10)));

        var outcome = coordinator.Supervise(session, messages.Add, cancel.Token);

        Assert.Equal(FinishStatus.Unchanged, outcome.Status);
        Assert.Single(messages, m => m.Contains("Second PC wants to use Medal", StringComparison.Ordinal));
    }

    [Fact]
    public void Supervise_SwitchesUploadLimitsWhenMedalExitsAndAFailedSwitchNeverBlocksFinish()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        pc.Mount.UploadLimitProblem = "Synthetic upload limit failure.";
        int polls = 0;
        var medal = pc.Medal;
        using var cancel = new CancellationTokenSource();
        var clock = new StoppingClock(_clock, () =>
        {
            if (++polls == 3) medal.Running = false;
        });
        var coordinator = new SessionCoordinator(_store,
            new SqliteLocalLibrary(pc.Database, MedalFixture.AccountId, _fixture.PathFor("first", "backups"), _clock),
            pc.Mount, medal, _assets, clock,
            new SessionOptions(FirstPc, WindowsPath.Parse(@"M:\Clips"), Versions, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10)));
        var messages = new List<string>();

        var outcome = coordinator.Supervise(session, messages.Add, cancel.Token);

        Assert.Equal(FinishStatus.Unchanged, outcome.Status);
        Assert.Single(messages, m => m == "Synthetic upload limit failure.");
        Assert.True(pc.Mount.UploadLimits.Count(r => r) >= 2);
        Assert.False(pc.Mount.UploadLimits[^1]);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void ShutdownDuringHydrationReturnsOwnershipWithoutCrashingOrLaunchingMedal()
    {
        var shutdown = new SessionShutdown(_clock);
        var replication = new FakeReplication { OnPrepare = () =>
        {
            shutdown.RequestEnd();
            _clock.Sleep(shutdown.Budget);
            shutdown.RequireTime();
        } };
        var pc = Computer(FirstPc, "first", shutdown: shutdown, replication: replication);
        Assert.Equal(LaunchStatus.Waiting, pc.Coordinator.Launch().Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(0, pc.Medal.Launches);
        Assert.Empty(pc.Mount.Starts);
    }

    [Fact]
    public void ShutdownDuringReplicationRetainsOwnershipAndMedia()
    {
        var shutdown = new SessionShutdown(_clock);
        var replication = new FakeReplication { OnPublish = () =>
        {
            shutdown.RequestEnd();
            _clock.Sleep(shutdown.Budget);
            shutdown.RequireTime();
        } };
        var pc = Computer(FirstPc, "first", shutdown: shutdown, replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Finish(session).Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void SupervisorBacksOffDuringAStorageOutageAndNotifiesOnce()
    {
        int attempts = 0;
        var replication = new FakeReplication { OnPublish = () => { if (++attempts <= 3) throw new StorageOfflineException("synthetic outage"); } };
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        var started = _clock.Now;
        List<string> notices = [];
        var finished = pc.Coordinator.Supervise(session, notices.Add, CancellationToken.None);
        Assert.Equal(FinishStatus.Unchanged, finished.Status);
        Assert.Equal(4, attempts);
        Assert.True(_clock.Now - started >= TimeSpan.FromSeconds(30 + 60 + 120));
        Assert.StartsWith("Waiting for storage", Assert.Single(notices));
    }

    [Theory]
    [MemberData(nameof(AlwaysFailingFinishes))]
    public void SupervisorBacksOffEveryFailedFinishAndStartsABoundedNumberOfAttemptsPerHour(string kind, int maxAttempts)
    {
        int attempts = 0;
        var replication = new FakeReplication { OnPublish = () =>
        {
            attempts++;
            throw kind switch
            {
                "action" => new StorageActionRequiredException("Primary storage onedrive:MedalSync already holds a different file at one of this library's clip paths."),
                "failure" => new IOException("synthetic provider failure"),
                _ => new StorageOfflineException("synthetic outage"),
            };
        } };
        List<SessionProgress> reports = [];
        var pc = Computer(FirstPc, "first", replication: replication, progress: reports.Add);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        var started = _clock.Now;
        using var cancel = new CancellationTokenSource();
        _clock.OnSleep = () => { if (_clock.Now - started >= TimeSpan.FromHours(1)) cancel.Cancel(); };
        List<string> notices = [];

        var outcome = pc.Coordinator.Supervise(session, notices.Add, cancel.Token);

        Assert.Equal(FinishStatus.Pending, outcome.Status);
        Assert.InRange(attempts, 5, maxAttempts);
        Assert.Single(notices);
        if (kind == "action")
        {
            Assert.StartsWith("Action needed before switching PCs: Primary storage onedrive:MedalSync already holds a different file", notices[0]);
            Assert.Equal(SessionPhase.RecoveryNeeded, reports.Last(r => r.IsOutcome).Phase);
        }
        // Ownership, cache and generation stay with this PC throughout.
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void MedalReopeningDuringFailureBackoffResumesSupervisionAtOnce()
    {
        var replication = new FakeReplication { OnPublish = () => throw new StorageActionRequiredException("synthetic conflict") };
        List<SessionProgress> reports = [];
        var pc = Computer(FirstPc, "first", replication: replication, progress: reports.Add);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        using var cancel = new CancellationTokenSource();
        int sleeps = 0;
        _clock.OnSleep = () =>
        {
            if (++sleeps == 1) pc.Medal.Running = true;
            if (sleeps == 3) cancel.Cancel();
        };

        pc.Coordinator.Supervise(session, _ => { }, cancel.Token);

        Assert.Equal(1, replication.Published);
        Assert.Equal(SessionPhase.Active, reports[^1].Phase);
    }

    [Fact]
    public void FailingNoticesNeverEndSupervision()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        _store.WriteRequest(new HandoffRequest(1, SecondPc, SessionCoordinator.NewId(), _clock.Now));
        int polls = 0;
        _clock.OnSleep = () => { if (++polls == 3) pc.Medal.Running = false; };
        int notices = 0;

        var outcome = pc.Coordinator.Supervise(session, _ =>
        {
            notices++;
            throw new IOException("synthetic full disk while writing the log");
        }, CancellationToken.None);

        Assert.Equal(1, notices);
        Assert.Equal(FinishStatus.Unchanged, outcome.Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void SupervisorNotifiesFreshRequestsOnceEachAndRemovesStaleOnes()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        var third = new DeviceIdentity("cccccccccccccccccccccccccccccccc", "Old PC");
        var fourth = new DeviceIdentity("dddddddddddddddddddddddddddddddd", "Skewed PC");
        _store.WriteRequest(new HandoffRequest(1, SecondPc, SessionCoordinator.NewId(), _clock.Now));
        _store.WriteRequest(new HandoffRequest(1, third, SessionCoordinator.NewId(), _clock.Now - TimeSpan.FromHours(7)));
        // A clock running a day ahead cannot keep an old request alive: the share's file time is old too.
        _store.WriteRequest(new HandoffRequest(1, fourth, SessionCoordinator.NewId(), _clock.Now + TimeSpan.FromDays(1)));
        File.SetLastWriteTimeUtc(Path.Combine(_store.Root, "requests", fourth.DeviceId + ".json"), (_clock.Now - TimeSpan.FromHours(7)).UtcDateTime);
        int polls = 0;
        using var cancel = new CancellationTokenSource();
        _clock.OnSleep = () =>
        {
            // The second PC asks again: a new request notifies again, even with the same wording.
            if (++polls == 2) _store.WriteRequest(new HandoffRequest(1, SecondPc, SessionCoordinator.NewId(), _clock.Now));
            if (polls == 4) cancel.Cancel();
        };
        List<string> notices = [];

        pc.Coordinator.Supervise(session, notices.Add, cancel.Token);

        Assert.Equal(2, notices.Count(n => n == "Second PC wants to use Medal. Quit Medal from its tray icon to switch."));
        Assert.DoesNotContain(notices, n => n.Contains("Old PC", StringComparison.Ordinal) || n.Contains("Skewed PC", StringComparison.Ordinal));
        Assert.Empty(_store.StaleRequests(_clock.Now));
        Assert.Equal(SecondPc, Assert.Single(_store.PendingRequests(_clock.Now)).Device);
    }

    [Fact]
    public void StaleSwitchRequestWarnsOnceAndKeepsSupervisingMedal()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        var request = new HandoffRequest(1, SecondPc, SessionCoordinator.NewId(), _clock.Now);
        _store.WriteRequest(request);
        int opens = 0;
        _store.RequestReader = new FreshCoordinationRead
        {
            ReadOpen = _ => { opens++; return (Json.Serialize(request), DateTime.UnixEpoch); },
            ReadListing = _ => DateTime.UnixEpoch.AddSeconds(1),
            Sleep = _ => { },
            Now = () => _clock.Now,
            PauseDirectory = _fixture.PathFor("request-pauses"),
        };
        int polls = 0;
        using var cancel = new CancellationTokenSource();
        _clock.OnSleep = () => { if (++polls == 3) cancel.Cancel(); };
        List<string> notices = [];

        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Supervise(session, notices.Add, cancel.Token).Status);
        Assert.Single(notices, n => n.StartsWith("Clip storage is unreachable", StringComparison.Ordinal));
        Assert.DoesNotContain(notices, n => n.Contains("wants to use Medal", StringComparison.Ordinal));
        Assert.Equal(FreshCoordinationRead.Attempts, opens);
        Assert.True(pc.Medal.Running);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }
}

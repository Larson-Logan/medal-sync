using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class SessionTests : IDisposable
{
    private const string LibraryId = "0123456789abcdef0123456789abcdef";
    private static readonly DeviceIdentity FirstPc = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "First PC");
    private static readonly DeviceIdentity SecondPc = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Second PC");
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");

    private readonly MedalFixture _fixture = new();
    private readonly CoordinationStore _store;

    public SessionTests()
    {
        string seed = _fixture.CreateLibrary("seed");
        MedalFixture.AddContent(seed, "clip-1", video: @"M:\Clips\Clips\one.mp4");
        _store = CoordinationStore.Create(_fixture.PathFor("server", LibraryId), LibraryId,
            LibraryDatabase.ReadSnapshot(seed, MedalFixture.AccountId), new AssetManifest(1, [], []), FirstPc,
            SessionCoordinator.NewId(), Versions, DateTimeOffset.UnixEpoch);
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        public Action? OnSleep { get; set; }

        public void Sleep(TimeSpan duration) { Now += duration; OnSleep?.Invoke(); }
    }

    private sealed class FakeMedal : IMedalProcess
    {
        public bool Running { get; set; }

        public int Launches { get; private set; }

        public Action? OnLaunch { get; set; }

        public bool IsRunning() => Running;

        public void Launch()
        {
            OnLaunch?.Invoke();
            Launches++;
            Running = true;
        }
    }

    private sealed class FakeCompatibility : ISessionCompatibility
    {
        public bool Supported { get; set; } = true;
        public MedalVersions CurrentVersions { get; set; } = Versions;
        public int Checks { get; private set; }
        public int PublishChecks { get; private set; }
        public string? Warning { get; set; }
        public bool CheckFails { get; set; }

        public void RequireLaunch()
        {
            if (!Supported) throw new IncompatibleLibraryException("Synthetic incompatible Medal update.");
        }

        public MedalVersions RequirePublish()
        {
            PublishChecks++;
            RequireLaunch();
            return CurrentVersions;
        }

        public string? CheckForUpdate()
        {
            Checks++;
            if (CheckFails) throw new IOException("Synthetic metadata read failure.");
            return Warning;
        }
    }

    private sealed class FakeMount : IClipMount
    {
        public bool IsRunning { get; private set; }

        public List<string> Starts { get; } = [];

        public List<bool> ForRecording { get; } = [];

        public List<string> Forgotten { get; } = [];

        public Action<string>? OnRetainAbortedStart { get; set; }
        public List<string> RetainedAbortedStarts { get; } = [];

        public MountHealth HealthResult { get; set; } = new(true, "ok");

        public Queue<MountHealth> HealthResults { get; } = new();

        public Queue<DrainVerdict> Verdicts { get; } = new();

        public DrainVerdict Steady { get; set; } = new(DrainState.Drained, [], 0, 0);

        public Action? OnStart { get; set; }
        public Action<string>? OnStarted { get; set; }

        public Action? OnDrain { get; set; }

        public Action? OnStop { get; set; }

        public bool StopResult { get; set; } = true;

        public bool RemainRunningOnStop { get; set; }

        public bool StopThrows { get; set; }

        public void Start(string session, bool forRecording)
        {
            OnStart?.Invoke();
            Starts.Add(session);
            ForRecording.Add(forRecording);
            IsRunning = true;
            OnStarted?.Invoke(session);
        }

        public MountHealth Health() => HealthResults.Count > 0 ? HealthResults.Dequeue() : HealthResult;

        public DrainVerdict Drain()
        {
            OnDrain?.Invoke();
            return Verdicts.Count > 0 ? Verdicts.Dequeue() : Steady;
        }

        public bool Stop()
        {
            if (StopThrows) throw new IOException("Synthetic mount stop failure.");
            IsRunning = RemainRunningOnStop;
            OnStop?.Invoke();
            return StopResult;
        }

        public void Forget(string session) => Forgotten.Add(session);

        public void RetainAbortedStart(string session)
        {
            OnRetainAbortedStart?.Invoke(session);
            RetainedAbortedStarts.Add(session);
        }
    }

    private sealed class FakeAssets : IAssetChecker
    {
        public HashSet<string> OnServerFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool MountBroken { get; set; }

        public AssetReport ThroughMount(LibrarySnapshot snapshot) => Report(snapshot, MountBroken ? _ => false : _ => true);

        public Action? OnServerCheck { get; set; }

        public AssetReport OnServer(LibrarySnapshot snapshot)
        {
            OnServerCheck?.Invoke();
            return Report(snapshot, OnServerFiles.Contains);
        }

        public bool ExistsOnServer(string capturePath) => OnServerFiles.Contains(capturePath);

        public bool ServerReachable { get; set; } = true;

        public void RequireServer()
        {
            if (!ServerReachable) throw new IOException("The clip folder on the server cannot be reached.");
        }

        private static AssetReport Report(LibrarySnapshot snapshot, Func<string, bool> exists) =>
            new(AssetInventory.Collect(snapshot)
                .Select(r => new AssetStatus(r, exists(r.Path) ? AssetProblem.None : AssetProblem.Missing, 1, DateTime.UnixEpoch, null))
                .ToArray());
    }

    private sealed record Pc(string Database, FakeMedal Medal, FakeMount Mount, SessionCoordinator Coordinator);

    private readonly FakeClock _clock = new();
    private readonly FakeAssets _assets = new() { OnServerFiles = { @"M:\Clips\Clips\one.mp4" } };

    private Pc Computer(DeviceIdentity device, string machine, bool acceptChanges = false, bool replaceLocal = false, string? database = null, Action? prepareNativeBackup = null,
        Func<LibraryChangeReview, bool>? reviewLibraryChanges = null, SessionShutdown? shutdown = null, IStorageReplication? replication = null,
        ISessionCompatibility? compatibility = null, Action<SessionProgress>? progress = null)
    {
        string db = database ?? _fixture.CreateLibrary(machine);
        var medal = new FakeMedal();
        var mount = new FakeMount();
        var options = new SessionOptions(device, WindowsPath.Parse(@"M:\Clips"), Versions,
            TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10), acceptChanges, replaceLocal);
        var library = new SqliteLocalLibrary(db, MedalFixture.AccountId, _fixture.PathFor(machine, "backups"), _clock);
        return new Pc(db, medal, mount, new SessionCoordinator(_store, library, mount, medal, _assets, _clock, options, prepareNativeBackup, reviewLibraryChanges, shutdown, replication, compatibility, progress));
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
    public void IncompatibleUpdateStillDrainsButRetainsOwnershipAndCacheUntilRetry()
    {
        var compatibility = new FakeCompatibility();
        var pc = Computer(FirstPc, "first", compatibility: compatibility);
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "after-update");
        pc.Medal.Running = false;
        compatibility.Supported = false;
        int drains = 0;
        pc.Mount.OnDrain = () => drains++;

        var blocked = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, blocked.Status);
        Assert.Equal(1, drains);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(FirstPc, _store.ReadJournal().Owner);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);

        compatibility.Supported = true;
        compatibility.CurrentVersions = Versions with { App = "2644.591.1" };
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(compatibility.CurrentVersions, _store.Generations.Read(1).Manifest.Medal);
        Assert.Contains(session, pc.Mount.Forgotten);
    }

    [Fact]
    public void UpdateDuringFinalVerificationRetainsSessionAndCanRetryWithCurrentVersions()
    {
        var compatibility = new FakeCompatibility();
        var pc = Computer(FirstPc, "first", compatibility: compatibility);
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "after-update");
        pc.Medal.Running = false;
        _assets.OnServerCheck = () => compatibility.CurrentVersions = Versions with { App = "2644.591.1" };

        var blocked = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, blocked.Status);
        Assert.Contains("updated while the handoff", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(compatibility.CurrentVersions, _store.Generations.Read(1).Manifest.Medal);
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

    private void Record(Pc pc, string id)
    {
        string path = $@"M:\Clips\Clips\{id}.mp4";
        MedalFixture.AddContent(pc.Database, id, video: path);
        _assets.OnServerFiles.Add(path);
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

    private (LocalState State, MedalSyncConfig Config) RecoverySetup()
    {
        var state = new LocalState(_fixture.PathFor("state"));
        var config = new MedalSyncConfig(1, LibraryId, MedalFixture.AccountId, _fixture.PathFor("server"),
            _fixture.PathFor("media"), @"M:\Clips", _fixture.PathFor("cache"), @"C:\Tools\rclone.exe",
            new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []) { StorageMode = StorageMode.Local };
        Directory.CreateDirectory(state.Root);
        File.WriteAllBytes(state.ConfigPath, Json.Serialize(config));
        _ = state.Device();
        return (state, config);
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
    public void ExplicitFinishRecoversStartingAfterRestartWithoutLaunchingOrDeletingCache()
    {
        var (state, config) = RecoverySetup();
        string session = SessionCoordinator.NewId();
        _store.Update(r => Ownership.Acquire(r, FirstPc, session, _clock.Now), TimeSpan.FromSeconds(1));
        var secret = state.SessionSecret(session, _clock.Now);
        Directory.CreateDirectory(config.SessionCacheDirectory(session));
        File.WriteAllText(Path.Combine(config.SessionCacheDirectory(session), "unknown"), "retain");
        // Simulate a crash after evidence was retained but before journal publication completed.
        state.RetainAbortedStart(session, LibraryId, config.SessionCacheDirectory(session), _clock.Now);
        Assert.Equal(secret, new LocalState(state.Root).SessionSecret(session, _clock.Now));
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(state.Root, false, false, _ => _store.ReadJournal()));
        var restarted = Computer(FirstPc, "restarted");
        restarted.Mount.OnRetainAbortedStart = id => state.RetainAbortedStart(id, LibraryId, config.SessionCacheDirectory(id), _clock.Now);
        Assert.Equal(FinishStatus.Unchanged, restarted.Coordinator.Finish(session).Status);
        Assert.Equal(0, restarted.Medal.Launches);
        Assert.Empty(restarted.Mount.Starts);
        Assert.Empty(restarted.Mount.Forgotten);
        Assert.Equal("retain", File.ReadAllText(Path.Combine(config.SessionCacheDirectory(session), "unknown")));
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FinalVerificationMutationRetainsOwnershipAndCacheUntilRetry(bool changedBeforeFinish, bool duringStop)
    {
        var pc = Computer(FirstPc, "first");
        string session = pc.Coordinator.Launch().Session!;
        if (changedBeforeFinish) Record(pc, "early");
        pc.Medal.Running = false;
        Action mutate = () => Record(pc, "late");
        if (duringStop) pc.Mount.OnStop = mutate;
        else _assets.OnServerCheck = mutate;

        var blocked = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, blocked.Status);
        Assert.Contains("library changed", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(session, _store.ReadJournal().SessionId);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.False(pc.Mount.IsRunning);
        Assert.Single(_store.Generations.Numbered());
        Assert.Equal(LaunchStatus.Waiting, Computer(SecondPc, "second").Coordinator.Launch().Status);

        pc.Mount.OnStop = null;
        _assets.OnServerCheck = null;
        var retried = pc.Coordinator.Finish(session);
        Assert.Equal(FinishStatus.Published, retried.Status);
        Assert.Equal(changedBeforeFinish ? 3 : 2, _store.Generations.Read(retried.Generation).Snapshot.RowCount("contents"));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Contains(session, pc.Mount.Forgotten);
    }

    [Fact]
    public void DeletingTheLastClipHandsOffAnEmptyGenerationWithBackupAndDeviceSettings()
    {
        var first = Computer(FirstPc, "first");
        var second = Computer(SecondPc, "second");
        AssertStarted(second.Coordinator.Launch());
        second.Medal.Running = false;
        Assert.Equal(FinishStatus.Unchanged, second.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        string secondSettings = MedalFixture.Settings(second.Database);
        var old = LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId);
        string[] earlierBackups = Directory.GetFiles(_fixture.PathFor("second", "backups"), "*.db");

        AssertStarted(first.Coordinator.Launch());
        MedalFixture.DeleteContent(first.Database, "clip-1");
        first.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, first.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        var empty = _store.Generations.Read(1).Snapshot;
        Assert.Equal(0, empty.RowCount("contents"));
        Assert.Equal(1, empty.RowCount("deleted_contents"));

        var launched = second.Coordinator.Launch();
        AssertStarted(launched);
        Assert.Contains("backed up", launched.Message, StringComparison.Ordinal);
        Assert.Equal(empty.Digest(), LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).Digest());
        Assert.Equal(secondSettings, MedalFixture.Settings(second.Database));
        string backup = Directory.GetFiles(_fixture.PathFor("second", "backups"), "*.db").Except(earlierBackups).Single();
        Assert.Equal(old.Digest(), LibraryDatabase.ReadSnapshot(backup, MedalFixture.AccountId).Digest());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyGenerationStillRequiresReviewToReplaceUnsharedWork(bool replaceLocal)
    {
        var first = Computer(FirstPc, "first");
        AssertStarted(first.Coordinator.Launch());
        MedalFixture.DeleteContent(first.Database, "clip-1");
        first.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, first.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        var second = Computer(SecondPc, "second", replaceLocal: replaceLocal);
        Record(second, "unshared");
        string before = LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).Digest();

        var launched = second.Coordinator.Launch();

        if (replaceLocal)
        {
            AssertStarted(launched);
            Assert.Contains("backed up", launched.Message, StringComparison.Ordinal);
            Assert.Equal(_store.Generations.Read(1).Snapshot.Digest(), LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).Digest());
        }
        else
        {
            Assert.Equal(LaunchStatus.Blocked, launched.Status);
            Assert.Contains("never shared", launched.Message, StringComparison.Ordinal);
            Assert.Equal(before, LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).Digest());
            Assert.Empty(second.Mount.Starts);
        }
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
    public void Finish_WaitsForUploadsAndNeverPublishesOnUnknownStatus()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        pc.Mount.Steady = DrainVerdict.Unknown("rclone did not answer vfs/stats.");

        var pending = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Pending, pending.Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);

        pc.Mount.Steady = new DrainVerdict(DrainState.Failed, ["1 file(s) failed to upload."], 1, 10);
        Assert.Equal(FinishStatus.Blocked, pc.Coordinator.Finish(session).Status);

        pc.Mount.Verdicts.Enqueue(new DrainVerdict(DrainState.Pending, ["1 upload(s) remaining"], 1, 10));
        pc.Mount.Steady = new DrainVerdict(DrainState.Drained, [], 0, 0);
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
    }

    [Fact]
    public void Finish_DoesNothingWhileMedalRuns()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        var outcome = pc.Coordinator.Finish(_store.ReadJournal().SessionId!);
        Assert.Equal(FinishStatus.Pending, outcome.Status);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }

    [Fact]
    public void Finish_EndsAnUnchangedSessionWithoutANewGeneration()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        pc.Medal.Running = false;

        var outcome = pc.Coordinator.Finish(_store.ReadJournal().SessionId!);

        Assert.Equal(FinishStatus.Unchanged, outcome.Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal([0L], _store.Generations.Numbered());
    }

    [Fact]
    public void Finish_BlocksWhenMedalDroppedClipsThatStillExist()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        string session = _store.ReadJournal().SessionId!;
        // Medal's missing-media handling during an outage clears the path.
        using (var connection = MedalFixture.Open(pc.Database))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET video_path = NULL WHERE local_content_id = 'clip-1'");
        }
        pc.Medal.Running = false;

        var blocked = pc.Coordinator.Finish(session);
        Assert.Equal(FinishStatus.Blocked, blocked.Status);
        Assert.Contains("audit", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);

        // A deliberate removal of the file too is a normal deletion.
        _assets.OnServerFiles.Remove(@"M:\Clips\Clips\one.mp4");
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopReview_AcceptsOnlyConfirmedRemovalsAndAllowsRelaunch(bool accept)
    {
        LibraryChangeReview? shown = null;
        var pc = Computer(FirstPc, "first", reviewLibraryChanges: review => { shown = review; return accept; });
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        pc.Medal.Running = false;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id = 'clip-1'");

        // Exercise the desktop restart path: it resolves the old session and then starts Medal.
        var outcome = pc.Coordinator.Launch();

        Assert.NotNull(shown);
        Assert.Equal(session, shown.SessionId);
        Assert.Single(shown.Changes);
        Assert.Equal(MediaChangeKind.RemovedWithoutRecord, shown.Changes[0].Kind);
        Assert.Equal(accept ? LaunchStatus.Started : LaunchStatus.Blocked, outcome.Status);
        Assert.Equal(accept ? 1 : 0, _store.ReadJournal().CommittedGeneration);
        Assert.Equal(accept ? 2 : 1, pc.Medal.Launches);
        Assert.Contains(@"M:\Clips\Clips\one.mp4", _assets.OnServerFiles);
        if (!accept) Assert.Empty(pc.Mount.Forgotten);
    }

    [Theory]
    [InlineData("library")]
    [InlineData("medal")]
    [InlineData("ownership")]
    public void DesktopReview_RefusesChangesMadeWhileTheDialogWasOpen(string changed)
    {
        Pc? pc = null;
        pc = Computer(FirstPc, "first", reviewLibraryChanges: review =>
        {
            if (changed == "library") MedalFixture.AddContent(pc!.Database, "unreviewed");
            if (changed == "medal") pc!.Medal.Running = true;
            if (changed == "ownership")
                _store.Update(r => Ownership.Abandon(r, SecondPc, review.SessionId, "test recovery", _clock.Now), TimeSpan.FromSeconds(1));
            return true;
        });
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        pc.Medal.Running = false;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id = 'clip-1'");

        var outcome = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, outcome.Status);
        Assert.Contains("changed during review", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void DesktopReview_DoesNotBypassRemainingMissingMedia()
    {
        var pc = Computer(FirstPc, "first", reviewLibraryChanges: _ => true);
        AssertStarted(pc.Coordinator.Launch());
        pc.Medal.Running = false;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id = 'clip-1'");
        MedalFixture.AddContent(pc.Database, "new-missing", video: @"M:\Clips\Clips\missing.mp4");

        var outcome = pc.Coordinator.Finish(_store.ReadJournal().SessionId!);

        Assert.Equal(FinishStatus.Blocked, outcome.Status);
        Assert.Contains("not on the server", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
    }

    [Fact]
    public void DesktopReview_IncludesCloudOnlyRemovalsAndPreservesBothSnapshots()
    {
        LibraryChangeReview? shown = null;
        var pc = Computer(FirstPc, "first", reviewLibraryChanges: review => { shown = review; return true; });
        AssertStarted(pc.Coordinator.Launch());
        MedalFixture.AddContent(pc.Database, "cloud-only", metadata: "{\"title\":\"\",\"remoteContent\":{\"contentTitle\":\"Cloud clip\"}}");
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        AssertStarted(pc.Coordinator.Launch());
        pc.Medal.Running = false;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents");

        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);

        Assert.NotNull(shown);
        Assert.Equal(2, shown.Changes.Count);
        string page = Assert.Single(shown.Pages());
        Assert.Contains("one.mp4", page, StringComparison.Ordinal);
        Assert.Contains("Cloud clip", page, StringComparison.Ordinal);
        Assert.Equal(2, shown.Before.RowCount("contents"));
        Assert.Equal(0, shown.After.RowCount("contents"));
    }

    [Fact]
    public void Finish_BlocksWhileAFileIsNotOnTheServer()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        MedalFixture.AddContent(pc.Database, "clip-2", video: @"M:\Clips\Clips\two.mp4");
        pc.Medal.Running = false;

        var outcome = pc.Coordinator.Finish(_store.ReadJournal().SessionId!);

        Assert.Equal(FinishStatus.Blocked, outcome.Status);
        Assert.Contains("two.mp4", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
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
    public void Finish_ReusesItsOwnPublishedGenerationAfterACrash()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        // Simulate a crash after the generation was written but before commit.
        pc.Mount.Stop();
        _store.Update(r => Ownership.BeginDrain(r, FirstPc, session, _clock.Now), TimeSpan.FromSeconds(1));
        _store.Update(r => Ownership.BeginPublish(r, FirstPc, session, _clock.Now), TimeSpan.FromSeconds(1));
        var snapshot = LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId);
        _store.Generations.Publish(1, 0, session, FirstPc, snapshot, new AssetManifest(1, [], []), Versions, _clock.Now);

        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(1, _store.ReadJournal().CommittedGeneration);
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
    public void Finish_KeepsTheSessionWhenMedalIsReopenedDuringTheDrain()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        pc.Mount.OnDrain = () => pc.Medal.Running = true;

        var outcome = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Pending, outcome.Status);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);

        pc.Mount.OnDrain = null;
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
    }

    [Fact]
    public void Finish_NeverPublishesWhileMedalRunsAfterTheDriveStopped()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        pc.Mount.OnStop = () => pc.Medal.Running = true;

        var outcome = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, outcome.Status);
        Assert.Contains("medal-sync finish", outcome.Message, StringComparison.Ordinal);
        var journal = _store.ReadJournal();
        Assert.Equal(OwnershipState.Draining, journal.State);
        Assert.Equal(FirstPc, journal.Owner);
        Assert.Equal(0, journal.CommittedGeneration);

        pc.Mount.OnStop = null;
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.False(pc.Mount.ForRecording[^1]);
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
    }

    [Fact]
    public void Finish_SkipsAGenerationLeftByAnAbandonedSession()
    {
        var first = Computer(FirstPc, "first");
        AssertStarted(first.Coordinator.Launch());
        string lost = _store.ReadJournal().SessionId!;
        Record(first, "clip-2");
        // The first PC wrote generation 1 and died before committing it; the owner gave its session up.
        _store.Generations.Publish(1, 0, lost, FirstPc, LibraryDatabase.ReadSnapshot(first.Database, MedalFixture.AccountId),
            new AssetManifest(1, [], []), Versions, _clock.Now);
        _store.Update(r => Ownership.Abandon(r, SecondPc, lost, "first PC lost", _clock.Now), TimeSpan.FromSeconds(1));

        var second = Computer(SecondPc, "second");
        AssertStarted(second.Coordinator.Launch());
        Assert.Equal(1, LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).RowCount("contents"));
        Record(second, "clip-3");
        second.Medal.Running = false;
        var outcome = second.Coordinator.Finish(_store.ReadJournal().SessionId!);

        Assert.Equal(FinishStatus.Published, outcome.Status);
        Assert.Equal(2, outcome.Generation);
        Assert.Equal(0, _store.Generations.ReadManifest(2).ParentGeneration);
        Assert.Equal(2, _store.ReadJournal().CommittedGeneration);
    }

    [Fact]
    public void Finish_WaitsWhenTheServerCannotBeReached()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        // During an outage every file looks deleted; that must not pass as a clean session.
        using (var connection = MedalFixture.Open(pc.Database))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET video_path = NULL WHERE local_content_id = 'clip-1'");
        }
        _assets.OnServerFiles.Clear();
        _assets.ServerReachable = false;
        pc.Medal.Running = false;

        var outcome = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Pending, outcome.Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
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

    /// <summary>Not running at the first check, running at every later one.</summary>
    private sealed class MedalOpenedLater : IMedalProcess
    {
        private int _checks;

        public bool IsRunning() => ++_checks > 1;

        public void Launch() => throw new InvalidOperationException("Medal must not be launched.");
    }

    private static void AssertStarted(LaunchOutcome outcome) =>
        Assert.True(outcome.Status == LaunchStatus.Started, $"{outcome.Status}: {outcome.Message}");

    private sealed class FakeReplication : IStorageReplication
    {
        public Action? OnPrepare { get; set; }
        public Action? OnPublish { get; set; }
        public int Published { get; private set; }
        public string? Warning { get; set; }
        public string? Prepare(LibrarySnapshot snapshot) { OnPrepare?.Invoke(); return Warning; }
        public IReadOnlyList<string> Deferred { get; set; } = [];
        public ReplicationResult Publish(LibrarySnapshot snapshot) { Published++; OnPublish?.Invoke(); return new(Deferred); }
    }

    [Fact]
    public void FailedReplicaRetainsOwnershipCacheAndGenerationThenRetries()
    {
        var replication = new FakeReplication { OnPublish = () => throw new IOException("provider quota") };
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Finish(session).Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.True(pc.Mount.IsRunning);
        replication.OnPublish = null;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(2, replication.Published);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void UnchangedLibraryStillRequiresReplicaVerification()
    {
        var replication = new FakeReplication();
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Unchanged, pc.Coordinator.Finish(session).Status);
        Assert.Equal(1, replication.Published);
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
    public void ReopeningMedalDuringReplicationPreventsPublication()
    {
        var replication = new FakeReplication();
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        replication.OnPublish = () => pc.Medal.Running = true;
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Finish(session).Status);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void OfflinePrimaryAtFinishWaitsCalmlyRetainsOwnershipAndFinishesWhenBack()
    {
        List<SessionProgress> reports = [];
        var replication = new FakeReplication { OnPublish = () => throw new StorageOfflineException("synthetic outage") };
        var pc = Computer(FirstPc, "first", replication: replication, progress: reports.Add);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        var waiting = pc.Coordinator.Finish(session);
        Assert.Equal(FinishStatus.Pending, waiting.Status);
        Assert.True(waiting.StorageOffline);
        Assert.StartsWith("Waiting for storage", waiting.Message);
        Assert.Equal(SessionPhase.WaitingForStorage, reports.Last(r => r.IsOutcome).Phase);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Forgotten);
        replication.OnPublish = null;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
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

    [Fact]
    public void DeferredOptionalReplicaStillReleasesTheLibraryAndIsNamed()
    {
        var replication = new FakeReplication { Deferred = ["cloud"] };
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        var finished = pc.Coordinator.Finish(session);
        Assert.Equal(FinishStatus.Published, finished.Status);
        Assert.Contains("'cloud'", finished.Message);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
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

    public static TheoryData<string, int> AlwaysFailingFinishes => new()
    {
        // Attempt starts in one hour: 30 s doubling to 10 minutes for failures, to 5 minutes for outages.
        { "action", 10 },
        { "failure", 10 },
        { "offline", 16 },
    };

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
    public void ActionNeededFinishSucceedsOnTheFirstRetryAfterTheCauseIsFixed()
    {
        bool broken = true;
        var replication = new FakeReplication { OnPublish = () =>
        {
            if (broken) throw new StorageActionRequiredException("Primary storage onedrive:MedalSync did not accept rclone's sign-in.");
        } };
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        _clock.OnSleep = () => broken = false;

        var outcome = pc.Coordinator.Supervise(session, _ => { }, CancellationToken.None);

        Assert.Equal(FinishStatus.Published, outcome.Status);
        Assert.Equal(2, replication.Published);
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

    private sealed class StoppingClock(IClock inner, Action onSleep) : IClock
    {
        public DateTimeOffset Now => inner.Now;

        public void Sleep(TimeSpan duration)
        {
            inner.Sleep(duration);
            onSleep();
        }
    }
}

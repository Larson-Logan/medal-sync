using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed partial class SessionTests : IDisposable
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

        public Func<bool>? Recording { get; set; }

        public bool IsRecording() => Recording?.Invoke() ?? false;

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

        public bool EmptyStoppedCache { get; set; }

        public bool StoppedCacheHoldsNoUploads(string session) => !IsRunning && EmptyStoppedCache;

        public Action? OnStart { get; set; }
        public Action<string>? OnStarted { get; set; }

        public Action? OnDrain { get; set; }

        public List<bool> UploadLimits { get; } = [];

        public string? UploadLimitProblem { get; set; }

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

        public string? UseUploadLimit(bool recording)
        {
            UploadLimits.Add(recording);
            return UploadLimitProblem;
        }

        /// <summary>Each release request, in order: whether the replay buffer was included.</summary>
        public List<bool> Releases { get; } = [];

        public string? ReleaseProblem { get; set; }

        public bool ReleaseThrows { get; set; }

        public string? ReleaseUploads(bool includeBuffer)
        {
            if (ReleaseThrows) throw new IOException("Synthetic control failure.");
            Releases.Add(includeBuffer);
            return ReleaseProblem;
        }

        public bool Stop()
        {
            if (StopThrows) throw new IOException("Synthetic mount stop failure.");
            IsRunning = RemainRunningOnStop;
            OnStop?.Invoke();
            return StopResult;
        }

        public List<long> ForgottenGenerations { get; } = [];

        public Exception? ForgetFailure { get; set; }

        public int FinishedCleanups { get; private set; }

        public List<string> FinishedLeft { get; } = [];

        public void Forget(string session, long generation)
        {
            Forgotten.Add(session);
            ForgottenGenerations.Add(generation);
            if (ForgetFailure is not null) throw ForgetFailure;
        }

        public IReadOnlyList<string> CleanupFinished()
        {
            FinishedCleanups++;
            return FinishedLeft;
        }

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
        ISessionCompatibility? compatibility = null, Action<SessionProgress>? progress = null, SpacePressureMonitor? space = null,
        Func<LaunchPreflight>? preflight = null, string? enrollmentFolder = null, bool askOwnerToSwitch = true,
        Func<bool>? isIdle = null, Func<LibrarySnapshot, Action, bool>? idleSync = null, IKeepAwake? keepAwake = null)
    {
        string db = database ?? _fixture.CreateLibrary(machine);
        var medal = new FakeMedal();
        var mount = new FakeMount();
        var options = new SessionOptions(device, WindowsPath.Parse(@"M:\Clips"), Versions,
            TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10), acceptChanges, replaceLocal)
        {
            EnrollmentFolder = enrollmentFolder,
            AskOwnerToSwitch = askOwnerToSwitch,
        };
        var library = new SqliteLocalLibrary(db, MedalFixture.AccountId, _fixture.PathFor(machine, "backups"), _clock);
        return new Pc(db, medal, mount, new SessionCoordinator(_store, library, mount, medal, _assets, _clock, options, prepareNativeBackup, reviewLibraryChanges, shutdown, replication, compatibility, progress, space, preflight, isIdle, idleSync, keepAwake: keepAwake));
    }

    private void Record(Pc pc, string id)
    {
        string path = $@"M:\Clips\Clips\{id}.mp4";
        MedalFixture.AddContent(pc.Database, id, video: path);
        _assets.OnServerFiles.Add(path);
    }

    private (LocalState State, MedalSyncConfig Config) RecoverySetup()
    {
        var state = new LocalState(_fixture.PathFor("state"));
        var config = new MedalSyncConfig(1, LibraryId, MedalFixture.AccountId, _fixture.PathFor("server"),
            _fixture.PathFor("media"), @"M:\Clips", _fixture.PathFor("cache"), @"C:\Tools\rclone.exe",
            new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []) { StorageMode = StorageMode.Local };
        Directory.CreateDirectory(state.Root);
        File.WriteAllBytes(state.ConfigPath, Json.Serialize(config));
        // This state is FirstPc's, so setup readiness recognizes the sessions the tests start as this PC's own.
        File.WriteAllBytes(Path.Combine(state.Root, "device.json"),
            Json.Serialize(new DeviceFile(1, FirstPc.DeviceId, $"{Environment.MachineName} ({Environment.UserName})")));
        Assert.Equal(FirstPc.DeviceId, state.Device().DeviceId);
        return (state, config);
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

    public static TheoryData<string, int> AlwaysFailingFinishes => new()
    {
        // Attempt starts in one hour: 30 s doubling to 10 minutes for failures, to 5 minutes for outages.
        { "action", 10 },
        { "failure", 10 },
        { "offline", 16 },
    };

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

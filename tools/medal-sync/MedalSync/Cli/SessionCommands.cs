using System.Runtime.Versioning;
using System.Text.Json;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Cli;

/// <summary>Commands that need this PC's configuration. The mutating ones require <c>--apply</c>.</summary>
[SupportedOSPlatform("windows")]
internal static class SessionCommands
{
    // OS compatibility protocol: older installed supervisors and setup use this same name.
    internal const string InstanceMutex = @"Local\LarsonServer.MedalSync";

    private sealed record Context(LocalState State, MedalSyncConfig Config, MedalInstall Install, DeviceIdentity Device, RotatingLog Log);

    private static Context Load()
    {
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        var install = MedalEnvironment.Locate();
        if (install.AccountId != config.AccountId)
        {
            throw new IncompatibleLibraryException($"Medal is signed in as account {install.AccountId}, but medal-sync is set up for {config.AccountId}.");
        }
        return new Context(state, config, install, state.Device(), new RotatingLog(state.LogDirectory));
    }

    public static int Inspect(Arguments args, TextWriter output)
    {
        bool json = args.Flag("--json");
        args.RequireAllUsed();
        var state = LocalState.ForCurrentUser();
        MedalSyncConfig? config = File.Exists(state.ConfigPath) ? MedalSyncConfig.Load(state.ConfigPath) : null;
        var report = Inventory.Collect(config, state, DateTimeOffset.UtcNow);
        output.WriteLine(json ? JsonSerializer.Serialize(report, Json.Options) : InventoryText.Render(report));
        return 0;
    }

    public static int Compatibility(Arguments args, TextWriter output)
    {
        bool json = args.Flag("--json");
        args.RequireAllUsed();
        return WriteCompatibility(MedalEnvironment.Locate(), json, output);
    }

    internal static int WriteCompatibility(MedalInstall install, bool json, TextWriter output)
    {
        var report = MedalCompatibility.Check(install);
        output.WriteLine(json ? JsonSerializer.Serialize(report, Json.Options) : report.Summary
            + (report.Compatible ? "" : " Keep unfinished sessions and caches on this PC; check for a Medal Sync update."));
        return report.Compatible ? 0 : 1;
    }

    public static int Status(Arguments args, TextWriter output)
    {
        bool json = args.Flag("--json");
        args.RequireAllUsed();
        var context = Load();
        var store = CoordinationStore.Open(context.Config.LibraryCoordinationDirectory, context.Config.LibraryId, context.Config.AccountId);
        var journal = store.ReadJournal();
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(journal, Json.Options));
            return 0;
        }
        output.WriteLine(StatusText.For(journal, context.Device, Directory.Exists(context.Config.DriveRoot.Display)));
        foreach (var request in store.PendingRequests()) output.WriteLine($"Waiting to switch: {request.Device.DisplayName} since {request.RequestedAt:yyyy-MM-dd HH:mm} UTC.");
        return 0;
    }

    /// <summary>Read-only status client: never creates device identity, mount credentials or a handoff request.</summary>
    internal static DesktopStatus ReadDesktopStatus()
    {
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        var device = state.ExistingDevice();
        var store = CoordinationStore.Open(config.LibraryCoordinationDirectory, config.LibraryId, config.AccountId);
        var journal = store.ReadJournal();
        _ = store.Generations.ReadManifest(journal.CommittedGeneration);
        ProgressEnvelope? report = null;
        try { report = Json.ReadFile<ProgressEnvelope>(Path.Combine(state.Root, "desktop-status.json"), "Desktop progress"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* Explicit unknown below. */ }
        var status = DesktopStatus.Resolve(journal, device, report, AnotherInstanceRunning(), DateTimeOffset.UtcNow);
        try
        {
            var notice = Json.ReadFile<DesktopNotice>(Path.Combine(state.Root, "desktop-notice.json"), "Desktop notice");
            if (DateTimeOffset.UtcNow - notice.At < TimeSpan.FromHours(1)) status = status with { Notice = notice.Message };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* Optional notice. */ }
        return status;
    }

    public static int MountPlanCommand(Arguments args, TextWriter output)
    {
        args.RequireAllUsed();
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        output.WriteLine("Control login comes from RCLONE_RC_USER / RCLONE_RC_PASS, never the command line:");
        output.WriteLine(string.Join(' ', MountPlan.Arguments(config.MountSettings("<session>", Path.Combine(state.LogDirectory, "rclone-<session>.log")))
            .Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a).Prepend(config.ExpandedRclonePath)));
        return 0;
    }

    /// <summary><c>init --apply</c>: creates the shared library (generation 0) from this PC's stopped library.</summary>
    public static int Init(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        args.RequireAllUsed();
        var context = Load();
        using var instance = SingleInstance();
        if (new MedalProcess(context.Install).IsRunning()) throw new UnsafeOperationException("Quit Medal from its tray icon first.");
        var snapshot = LibraryDatabase.ReadSnapshot(context.Install.DatabasePath, context.Install.AccountId);
        var checker = new ShareAssetChecker(context.Config.Capture, context.Config.ServerCapture, context.Config.LibraryId, new FileSystemProbe());
        var onServer = checker.OnServer(snapshot);
        var blocking = onServer.Blocking.ToList();
        output.WriteLine($"Library: {LibraryCommands.Counts(snapshot)}; {onServer.Items.Count} file references, {blocking.Count} not usable on the server.");
        if (blocking.Count > 0)
        {
            foreach (var item in blocking.Take(10)) output.WriteLine($"  {item.Reference.Path}: {item.Problem}");
            throw new UnsafeOperationException("Every library file must be on the server (Medal's own storage move) before the shared library is created.");
        }
        if (!apply)
        {
            output.WriteLine($"Preview only. With --apply this creates {context.Config.LibraryCoordinationDirectory} and the storage marker.");
            return 0;
        }
        StorageIdentity.Initialize(context.Config.ServerCapture.Display, context.Config.LibraryId);
        CoordinationStore.Create(context.Config.LibraryCoordinationDirectory, context.Config.LibraryId, snapshot, AssetManifest.From(onServer),
            context.Device, SessionCoordinator.NewId(), context.Install.Versions, DateTimeOffset.UtcNow);
        context.Log.Write($"init: generation 0 created from {LibraryCommands.Counts(snapshot)}");
        output.WriteLine("Shared library created as generation 0.");
        return 0;
    }

    /// <summary>
    /// <c>launch</c>: starts Medal through medal-sync and supervises it until it exits.
    /// <paramref name="notify"/> also receives the supervisor's messages (the
    /// windowless launcher shows them).
    /// </summary>
    public static int Launch(Arguments args, TextWriter output, Action<string>? notify = null,
        Func<LibraryChangeReview, bool>? reviewLibraryChanges = null, SessionShutdown? shutdown = null)
    {
        bool replaceLocal = args.Flag("--replace-local-library");
        var wait = WaitSeconds(args.Value("--wait-for-server"));
        string? link = args.Value("--medal-link");
        if (link is not null) link = MedalLink.Require(link);
        args.RequireAllUsed();
        var context = Load();
        using var instance = SingleInstance();
        shutdown ??= new SessionShutdown(new SystemClock());
        using var status = new DesktopStatusPublisher(context.State, context.Config.LibraryId, context.Config.AccountId, context.Device.DeviceId);
        using var sessionEnd = new WindowsSessionEnd(shutdown, context.Log.Write);
        // At sign-in the network or WARP can still be starting; an unreachable
        // server after the wait is reported by the normal checks below.
        if (!Waiting.Until(() => shutdown.Ending || Directory.Exists(context.Config.CoordinationRoot), wait, TimeSpan.FromSeconds(5), new SystemClock()))
        {
            context.Log.Write($"launch: the server was still unreachable after {wait.TotalSeconds:0} s");
        }
        if (shutdown.Ending) return 3;
        GuardStartup(context);
        var (coordinator, store, mount) = Coordinator(context, acceptChanges: false, replaceLocal, reviewLibraryChanges, shutdown, link, status.Report);
        var journal = store.ReadJournal();
        if (journal.Owner?.DeviceId == context.Device.DeviceId) mount.Attach(journal.SessionId!);
        var outcome = coordinator.Launch();
        Report(context, output, $"launch: {outcome.Status}: {outcome.Message}");
        if (outcome.Status is not (LaunchStatus.Started or LaunchStatus.AlreadyRunning)) return outcome.Status == LaunchStatus.Waiting ? 3 : 1;
        // A recovered, already-running session must pass the coordinator's drive
        // and ownership checks before it receives a link.
        if (outcome.Status == LaunchStatus.AlreadyRunning && link is not null
            && !shutdown.TryLaunch(() => new MedalProcess(context.Install).Open(link))) return 3;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        FinishOutcome finished;
        // A Medal update can restore its own startup entries while it runs.
        using (new Timer(_ => { if (!shutdown.Ending) GuardStartup(context); }, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1)))
        {
            finished = coordinator.Supervise(outcome.Session!, m =>
            {
                Report(context, output, m);
                if (!shutdown.SuppressUi) notify?.Invoke(m);
            }, cancel.Token);
        }
        if (!shutdown.Ending) GuardStartup(context);
        Report(context, output, $"finish: {finished.Status}: {finished.Message}");
        return finished.Status is FinishStatus.Published or FinishStatus.Unchanged ? 0 : finished.Status == FinishStatus.Pending ? 3 : 1;
    }

    private static TimeSpan WaitSeconds(string? value)
    {
        if (value is null) return TimeSpan.Zero;
        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds) && seconds <= 600
            ? TimeSpan.FromSeconds(seconds)
            : throw new UsageException("--wait-for-server takes a whole number of seconds up to 600.");
    }

    /// <summary>Keeps the Run value and medal:// handler on the launcher once startup is rewired; never fails a launch.</summary>
    private static void GuardStartup(Context context)
    {
        // Machine-wide setup owns its own shortcuts, not Medal's per-user hooks.
        // Keep the existing portable integration intact until explicitly migrated.
        if (LocalState.IsManagedInstall(AppContext.BaseDirectory)) return;
        string launcher = Path.Combine(AppContext.BaseDirectory, StartupEntries.LauncherFile);
        try
        {
            foreach (string change in StartupEntries.Enforce(context.State.Root, launcher)) context.Log.Write("startup: " + change);
        }
        catch (Exception ex)
        {
            // It also runs on a timer thread, where an unhandled exception would end the supervisor.
            context.Log.Write("startup: check failed: " + ex.Message);
        }
    }

    /// <summary>True while another medal-sync on this PC (for example a supervising launcher) holds the instance lock.</summary>
    internal static bool AnotherInstanceRunning()
    {
        using var mutex = new Mutex(initiallyOwned: false, InstanceMutex);
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero)) return true;
        }
        catch (AbandonedMutexException)
        {
            // A crashed instance left it; it is ours now and nobody else holds it.
        }
        mutex.ReleaseMutex();
        return false;
    }

    public static int Finish(Arguments args, TextWriter output)
    {
        bool accept = args.Flag("--accept-library-changes");
        args.RequireAllUsed();
        var context = Load();
        using var instance = SingleInstance();
        var shutdown = new SessionShutdown(new SystemClock());
        using var sessionEnd = new WindowsSessionEnd(shutdown, context.Log.Write);
        using var status = new DesktopStatusPublisher(context.State, context.Config.LibraryId, context.Config.AccountId, context.Device.DeviceId);
        var (coordinator, store, mount) = Coordinator(context, accept, shutdown: shutdown, progress: status.Report);
        var journal = store.ReadJournal();
        if (journal.Owner?.DeviceId != context.Device.DeviceId)
        {
            output.WriteLine("This PC has no unfinished session.");
            return 0;
        }
        mount.Attach(journal.SessionId!);
        var finished = coordinator.Finish(journal.SessionId!);
        Report(context, output, $"finish: {finished.Status}: {finished.Message}");
        return finished.Status is FinishStatus.Published or FinishStatus.Unchanged ? 0 : finished.Status == FinishStatus.Pending ? 3 : 1;
    }

    /// <summary><c>abandon --session ID --reason TEXT --apply</c>: explicit recovery when the owner PC is lost.</summary>
    public static int Abandon(Arguments args, TextWriter output)
    {
        string session = args.Required("--session");
        string reason = args.Required("--reason");
        bool apply = args.Flag("--apply");
        args.RequireAllUsed();
        var context = Load();
        using var instance = SingleInstance();
        var store = CoordinationStore.Open(context.Config.LibraryCoordinationDirectory, context.Config.LibraryId, context.Config.AccountId);
        var journal = store.ReadJournal();
        output.WriteLine(StatusText.For(journal, context.Device));
        output.WriteLine($"Abandoning returns the library to generation {journal.CommittedGeneration}. Clips and edits made in session {journal.SessionId} "
            + $"that were not published stay only on {journal.Owner?.DisplayName ?? "that PC"} and its cache, and must be reconciled by hand.");
        if (!apply)
        {
            output.WriteLine("Preview only; add --apply to abandon.");
            return 0;
        }
        var next = store.Update(r => Ownership.Abandon(r, context.Device, session, reason, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        context.Log.Write($"abandon: {next.Reason}");
        output.WriteLine($"Done: {next.Reason}");
        return 0;
    }

    /// <summary>
    /// <c>spike start|status|stop --config FILE</c>: the Gate B mount experiment with
    /// the production mount code, against a disposable remote only. It never
    /// touches the journal, the library database or Medal's settings.
    /// </summary>
    public static int Spike(string action, Arguments args, TextWriter output)
    {
        string configPath = args.Required("--config");
        args.RequireAllUsed();
        var config = MedalSyncConfig.Load(configPath);
        if (!config.RemoteRoot.Segments.LastOrDefault("").Contains("spike", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeOperationException("The spike only runs against a disposable remote whose folder name contains 'spike'.");
        }
        var state = LocalState.ForCurrentUser();
        string sessionFile = Path.Combine(state.Root, "spike-session.txt");
        var clock = new SystemClock();
        var mount = new RcloneClipMount(config, state, 15, clock);
        switch (action)
        {
            case "start":
                Directory.CreateDirectory(config.ServerCapture.Display);
                string marker = config.ServerCapture.Append(MedalSyncConfig.StorageMarker).Display;
                if (!File.Exists(marker)) AtomicFile.CreateNew(marker, System.Text.Encoding.UTF8.GetBytes(config.LibraryId + "\n"));
                string session = SessionCoordinator.NewId();
                Directory.CreateDirectory(state.Root);
                File.WriteAllText(sessionFile, session);
                mount.Start(session, forRecording: true);
                output.WriteLine($"Spike mounted: {mount.Health().Detail} Capture folder for the test: {config.Capture.Display}");
                return 0;
            case "status":
            case "stop":
                if (!File.Exists(sessionFile)) throw new UnsafeOperationException("No spike session is recorded on this PC.");
                mount.Attach(File.ReadAllText(sessionFile).Trim());
                if (action == "status")
                {
                    var health = mount.Health();
                    var drain = mount.Drain();
                    output.WriteLine($"Health: {(health.Healthy ? "ok" : "PROBLEM")} - {health.Detail}");
                    output.WriteLine($"Uploads: {drain.State} {string.Join(" ", drain.Reasons)}");
                    return health.Healthy ? 0 : 1;
                }
                var beforeStop = mount.Drain();
                if (beforeStop.State != DrainState.Drained)
                    throw new UnsafeOperationException($"Spike uploads are {beforeStop.State}; keep the mount and cache until status says Drained. {string.Join(" ", beforeStop.Reasons)}");
                bool stopped = mount.Stop();
                output.WriteLine(stopped ? "Spike drive stopped; its cache folder is kept for inspection." : "The spike drive is still running.");
                return stopped ? 0 : 1;
            default:
                throw new UsageException("spike takes start, status or stop.");
        }
    }

    private static (SessionCoordinator, CoordinationStore, RcloneClipMount) Coordinator(Context context, bool acceptChanges,
        bool replaceLocal = false, Func<LibraryChangeReview, bool>? reviewLibraryChanges = null, SessionShutdown? shutdown = null,
        string? launchLink = null, Action<SessionProgress>? progress = null)
    {
        var clock = new SystemClock();
        var store = CoordinationStore.Open(context.Config.LibraryCoordinationDirectory, context.Config.LibraryId, context.Config.AccountId);
        double bitrate;
        using (var connection = LibraryDatabase.OpenReadOnly(context.Install.DatabasePath))
        {
            bitrate = double.TryParse(LibraryDatabase.ReadSetting(connection, null, "Bitrate"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double b) && b > 0 ? b : throw new UnsafeOperationException("Medal's recording bitrate is not set.");
        }
        var mount = new RcloneClipMount(context.Config, context.State, bitrate, clock);
        var library = new SqliteLocalLibrary(context.Install.DatabasePath, context.Install.AccountId, context.State.BackupDirectory, clock);
        var checker = new ShareAssetChecker(context.Config.Capture, context.Config.ServerCapture, context.Config.LibraryId, new FileSystemProbe());
        var options = new SessionOptions(context.Device, context.Config.Capture, context.Install.Versions,
            TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), acceptChanges, replaceLocal);
        return (new SessionCoordinator(store, library, mount, new MedalProcess(context.Install, launchLink), checker, clock, options,
            () => NativeBackupCompatibility.Prepare(context.Config, context.Install, context.State),
            reviewLibraryChanges is null ? null : review =>
            {
                if (shutdown?.SuppressUi == true || !reviewLibraryChanges(review) || shutdown?.SuppressUi == true) return false;
                // Retain both sides and the exact approval before publishing. Failure to save blocks acceptance.
                SaveReviewedChanges(context.State.BackupDirectory, review);
                context.Log.Write($"review: accepted {review.Changes.Count} change(s) in session {review.SessionId}, digest {review.After.Digest()}");
                return true;
            }, shutdown, context.Config.Replication is null ? null : new StorageReplication(context.Config, context.State, () =>
            {
                shutdown?.RequireTime();
                if (new MedalProcess(context.Install).IsRunning())
                    throw new IOException("Medal reopened during replication. Quit normally, then retry finish on this PC.");
            }, progress: progress), new MedalCompatibilityGuard(context.Install), progress), store, mount);
    }

    internal static string SaveReviewedChanges(string backupDirectory, LibraryChangeReview review)
    {
        string directory = Path.Combine(backupDirectory, "accepted-changes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        AtomicFile.CreateNew(Path.Combine(directory, "before.json"), review.Before.ToCanonicalJson());
        AtomicFile.CreateNew(Path.Combine(directory, "accepted.json"), review.After.ToCanonicalJson());
        AtomicFile.CreateNew(Path.Combine(directory, "review.json"), Json.Serialize(new
        {
            review.SessionId, review.Generation, BeforeDigest = review.Before.Digest(), AcceptedDigest = review.After.Digest(),
            review.Changes, AcceptedAt = DateTimeOffset.UtcNow,
        }));
        return directory;
    }

    private static void Report(Context context, TextWriter output, string message)
    {
        context.Log.Write(message);
        output.WriteLine(message);
    }

    internal static IDisposable SingleInstance(string mutexName = InstanceMutex)
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero)) return new InstanceLock(mutex);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance crashed; the mutex is now ours.
            return new InstanceLock(mutex);
        }
        mutex.Dispose();
        throw new UnsafeOperationException("medal-sync is already running on this PC.");
    }

    /// <summary>Holds the per-user mutex so only one medal-sync instance acts at a time.</summary>
    private sealed class InstanceLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}

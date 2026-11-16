using System.Data.Common;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Session;

internal enum LaunchStatus
{
    Started,
    AlreadyRunning,
    Waiting,
    Blocked,
}

/// <param name="Warning">Medal started, but something the user must fix will stop the handoff; shown as a passive notice.</param>
/// <param name="Wait">Whether an explicit handoff wait may keep polling after this outcome.</param>
/// <param name="Journal">The journal that showed another PC as owner, for <see cref="LaunchWait.OwnedElsewhere"/>.</param>
internal sealed record LaunchOutcome(LaunchStatus Status, string Message, string? Session = null, bool StorageOffline = false, string? Warning = null,
    LaunchWait Wait = LaunchWait.None, OwnershipRecord? Journal = null);

/// <summary>
/// The result of a read-only check that runs before ownership is taken. <paramref name="Refusal"/> keeps Medal closed
/// and the library Ready; <paramref name="Warning"/> starts Medal with a passive notice.
/// </summary>
internal sealed record LaunchPreflight(string? Warning, string? Refusal)
{
    public static readonly LaunchPreflight Proceed = new(null, null);
}

internal enum FinishStatus
{
    Published,
    Unchanged,
    Pending,
    Blocked,
}

/// <summary>Why a finish attempt is pending; every cause but <see cref="None"/> is retried with backoff.</summary>
internal enum PendingCause
{
    /// <summary>Nothing failed: uploads are still running, Medal is open, or another PC briefly holds the lock.</summary>
    None,

    /// <summary>Primary storage, a required replica or the journal cannot be reached right now.</summary>
    StorageOffline,

    /// <summary>The attempt failed for another reason that may clear by itself.</summary>
    Failed,

    /// <summary>Storage refused the work in a way only the user can fix: sign-in, configuration, a conflict, quota, failed verification.</summary>
    NeedsAction,
}

internal sealed record FinishOutcome(FinishStatus Status, string Message, long Generation, PendingCause Cause = PendingCause.None)
{
    /// <summary>Pending only because storage or the journal is unreachable; retried automatically.</summary>
    public bool StorageOffline => Cause == PendingCause.StorageOffline;
}

/// <summary>
/// The launch and handoff sequence. Launch: take ownership of a clean
/// generation, restore it into the local library, mount storage, check every
/// file, then start Medal. Finish: after Medal exits, drain uploads, audit the
/// library, verify files on the server, stop the mount, publish the next
/// generation and release ownership. Every failure leaves ownership with this
/// PC (or returns it untouched before Medal ever ran), never with nobody.
/// </summary>
internal sealed partial class SessionCoordinator(
    CoordinationStore store,
    ILocalLibrary library,
    IClipMount mount,
    IMedalProcess medal,
    IAssetChecker assets,
    IClock clock,
    SessionOptions options,
    Action? prepareNativeBackup = null,
    Func<LibraryChangeReview, bool>? reviewLibraryChanges = null,
    SessionShutdown? shutdown = null,
    IStorageReplication? replication = null,
    ISessionCompatibility? compatibility = null,
    Action<SessionProgress>? progress = null,
    SpacePressureMonitor? space = null,
    Func<LaunchPreflight>? preflight = null,
    Func<bool>? isIdle = null,
    Func<LibrarySnapshot, Action, bool>? idleSync = null,
    Func<Action, IAssetChecker>? idleAssets = null)
{
    private const string QuitFromTray = "Quit Medal from its tray icon";
    private const string ActiveMessage = "Medal is open. Quit Medal normally from its tray icon when you want to switch PCs.";
    private const string RecordingMessage = "Medal is recording a game. Quit Medal normally from its tray icon when you want to switch PCs.";
    private const string ShutdownMessage = "Windows is shutting down; Medal was not started.";
    private const string WaitCancelledMessage = "Stopped waiting for the other PC. Medal was not started and this PC's switch request was withdrawn.";
    private string? _uploadSession;
    private readonly Dictionary<ulong, (DateTimeOffset Since, long Tries)> _uploadRetries = [];

    // The switch request the last plain launch wrote. A wait the user chooses right after it keeps this one request,
    // so the owner is told once that this PC wants Medal, not once for the launch and again for the wait.
    private HandoffRequest? _sentRequest;

    // Set only while LaunchWhenReleased runs: its one switch request and the caller's cancellation.
    private HandoffRequest? _waitRequest;
    private bool _waitRequestSent;
    private Func<bool>? _waitCancelled;
    private DateTimeOffset? _waitDeadline;

    private DeviceIdentity Me => options.Device;

    private static bool IsExpected(Exception ex) =>
        ex is MedalSyncException or IOException or InvalidDataException or UnauthorizedAccessException or DbException;

    /// <summary>Recording for the advisory status; idle sync separately refuses a failed activity check.</summary>
    private bool IsRecording()
    {
        try { return medal.IsRecording(); }
        catch (Exception) { return false; }
    }

    private void Report(SessionPhase phase, string action, string? session = null, int? files = null, long? bytes = null, string? replica = null,
        bool isOutcome = false, bool recording = false, bool clipsSynced = false)
    {
        // A disconnected or failed view must never change ownership, uploads or recovery.
        try { progress?.Invoke(new(phase, action, session, files, bytes, replica, isOutcome, recording, clipsSynced)); }
        catch (Exception) { /* Telemetry is advisory. */ }
    }

    internal static string NewId() => Guid.NewGuid().ToString("N");
}

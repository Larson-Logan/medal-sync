using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Session;

/// <summary>Medal's own processes on this PC.</summary>
internal interface IMedalProcess
{
    /// <summary>True while any Medal process (UI, tray, recorder, encoder) is alive.</summary>
    bool IsRunning();

    /// <summary>Starts Medal normally, as the user, without elevation.</summary>
    void Launch();
}

/// <summary>Read-only update checks; never stop Medal or discard session data.</summary>
internal interface ISessionCompatibility
{
    void RequireLaunch();
    MedalVersions RequirePublish();
    string? CheckForUpdate();
}

/// <summary>This PC's own Medal library database.</summary>
internal interface ILocalLibrary
{
    string AccountId { get; }

    LibrarySnapshot Read();

    /// <summary>Empty replacement requires a checked committed history or explicit replacement review.</summary>
    RestoreResult Restore(LibrarySnapshot snapshot, bool allowEmptyReplacement = false);

    /// <summary>Medal's configured capture root (<c>ClipFolder</c>), or null when unset.</summary>
    string? CaptureFolder();
}

internal sealed record MountHealth(bool Healthy, string Detail, bool Retryable = false);

/// <summary>The SSD-cached virtual drive for one session.</summary>
internal interface IClipMount
{
    bool IsRunning { get; }

    /// <summary>
    /// Starts the mount with this session's cache folder, reusing it if it already
    /// exists. Only a recording start must prove the SSD can hold a whole session;
    /// a start that only finishes uploads needs no new space.
    /// </summary>
    void Start(string session, bool forRecording);

    MountHealth Health();

    DrainVerdict Drain();

    /// <summary>
    /// Applies the configured upload limit for recording, or for finishing uploads after Medal exits.
    /// Null when it is in place; otherwise a notice. Never throws: a limit only changes speed.
    /// </summary>
    string? UseUploadLimit(bool recording) => null;

    /// <summary>Stops gracefully. False when it is still running afterwards.</summary>
    bool Stop();

    /// <summary>Removes a finished, fully uploaded session's cache folder and control login.</summary>
    void Forget(string session);

    /// <summary>Retains a verified stopped pre-launch session's login and cache as recovery evidence, without deleting bytes.</summary>
    void RetainAbortedStart(string session);
}

/// <summary>Checks a library's files through the mount and directly on the server.</summary>
internal interface IAssetChecker
{
    AssetReport ThroughMount(LibrarySnapshot snapshot);

    AssetReport OnServer(LibrarySnapshot snapshot);

    /// <summary>Whether a capture-root path (M:\Clips\...) exists on the server, bypassing the mount.</summary>
    bool ExistsOnServer(string capturePath);

    /// <summary>Bytes of a capture-root file on the server, or null when it is absent or its size is unknown. Only budgets downloads.</summary>
    long? SizeOnServer(string capturePath) => null;

    /// <summary>
    /// Proves the server's clip folder is reachable and is this library's, so
    /// "not on the server" means absent rather than unreachable. Throws
    /// <see cref="IOException"/> when it cannot be reached.
    /// </summary>
    void RequireServer();
}

internal interface IClock
{
    DateTimeOffset Now { get; }

    void Sleep(TimeSpan duration);
}

/// <summary>Hydrates persistent local media and verifies primary storage and required replicas before handoff.</summary>
internal interface IStorageReplication
{
    /// <summary>Null when ready; otherwise a warning that recording can go ahead locally but the handoff cannot finish until the user fixes something.</summary>
    string? Prepare(LibrarySnapshot snapshot);

    /// <summary>Throws while primary storage or a required replica is unverified; names optional replicas left for a retry.</summary>
    ReplicationResult Publish(LibrarySnapshot snapshot);
}

internal sealed record ReplicationResult(IReadOnlyList<string> Deferred)
{
    public static readonly ReplicationResult None = new([]);
}

internal sealed record PendingReplica(string Name, DateTimeOffset Since, DateTimeOffset LastAttempt);

internal sealed record PendingReplicas(int Format, string LibraryId, IReadOnlyList<PendingReplica> Targets);

internal sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}

internal sealed record SessionOptions(
    DeviceIdentity Device,
    WindowsPath CaptureFolder,
    MedalVersions Medal,
    TimeSpan LockTimeout,
    TimeSpan DrainTimeout,
    TimeSpan PollInterval,
    bool AcceptLibraryChanges = false,
    bool ReplaceLocalLibrary = false);

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

/// <summary>This PC's own Medal library database.</summary>
internal interface ILocalLibrary
{
    string AccountId { get; }

    LibrarySnapshot Read();

    RestoreResult Restore(LibrarySnapshot snapshot);

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

    /// <summary>Stops gracefully. False when it is still running afterwards.</summary>
    bool Stop();

    /// <summary>Removes a finished, fully uploaded session's cache folder and control login.</summary>
    void Forget(string session);
}

/// <summary>Checks a library's files through the mount and directly on the server.</summary>
internal interface IAssetChecker
{
    AssetReport ThroughMount(LibrarySnapshot snapshot);

    AssetReport OnServer(LibrarySnapshot snapshot);

    /// <summary>Whether a capture-root path (M:\Clips\...) exists on the server, bypassing the mount.</summary>
    bool ExistsOnServer(string capturePath);

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

/// <summary>Hydrates persistent local media and verifies required replicas before handoff.</summary>
internal interface IStorageReplication
{
    void Prepare(LibrarySnapshot snapshot);
    void Publish(LibrarySnapshot snapshot);
}

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

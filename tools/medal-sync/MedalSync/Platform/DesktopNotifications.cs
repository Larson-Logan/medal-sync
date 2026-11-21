using MedalSync.Config;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Platform;

internal enum DesktopNotificationKind { Ready, NeedsAction, StorageAttention }

/// <summary>An advisory Windows notification. Its text is what the status panel already shows; clicking it opens the panel.</summary>
internal sealed record DesktopNotification(DesktopNotificationKind Kind, string Title, string Message);

/// <summary>
/// Turns successive status reads into at most one notification per state change. The first read after the status
/// view starts only sets the baseline, so a restart never repeats a notice. An unreadable or stale read
/// (<see cref="SessionPhase.Unknown"/>) carries no information and changes nothing, so a brief disconnect cannot
/// make the same notice appear twice. Waiting for storage resolves on its own and never notifies.
/// </summary>
internal sealed class DesktopNotifier
{
    internal const string ReadyTitle = "Ready to switch PCs";
    internal const string ReadyMessage = "Your clips are uploaded and verified. You can switch PCs.";
    internal const string NeedsActionTitle = "Medal Sync needs you";
    internal const string StorageTitle = "Storage needs attention";

    private bool _started;
    private SessionPhase _phase;
    private bool _finishing;
    private string? _attention;

    public DesktopNotification? Observe(DesktopStatus status)
    {
        string? attention = status.StorageAttention;
        if (status.Phase == SessionPhase.Unknown) return null;
        if (!_started)
        {
            (_started, _phase, _attention) = (true, status.Phase, attention);
            // Opened mid-finish: only this PC's own session counts, never another PC's recovery or a refused storage access.
            _finishing = status.OwnedHere && IsFinishing(status.Phase);
            return null;
        }
        var previous = _phase;
        _phase = status.Phase;
        string? previousAttention = _attention;
        _attention = attention;
        switch (status.Phase)
        {
            case SessionPhase.Active or SessionPhase.Uploading or SessionPhase.Verifying:
                // This PC owns a session, so the next Ready is its own finish, even if a quick one skips the upload phases.
                _finishing = true;
                break;
            case SessionPhase.Ready:
                bool finished = _finishing;
                _finishing = false;
                if (finished && previous != SessionPhase.Ready) return new(DesktopNotificationKind.Ready, ReadyTitle, ReadyMessage);
                break;
            case SessionPhase.RecoveryNeeded:
                // Only this PC's own finish: another PC's interrupted session also reads as recovery needed here.
                // The refusal keeps ownership, so a retried finish that reaches Ready still notifies.
                if (_finishing && previous != SessionPhase.RecoveryNeeded)
                    return new(DesktopNotificationKind.NeedsAction, NeedsActionTitle, Reason(status));
                break;
            case SessionPhase.Starting or SessionPhase.Waiting:
                // Starting before any session, or another PC holds the library: nothing of this PC's is finishing.
                _finishing = false;
                break;
            // Waiting for storage keeps whatever was finishing: it resolves by itself and never notifies.
        }
        if (attention is not null && attention != previousAttention)
            return new(DesktopNotificationKind.StorageAttention, StorageTitle, attention);
        return null;
    }

    private static bool IsFinishing(SessionPhase phase) => phase is SessionPhase.Active or SessionPhase.Uploading
        or SessionPhase.Verifying or SessionPhase.RecoveryNeeded;

    private static string Reason(DesktopStatus status) => status.LastResult is { Length: > 0 } last
        ? status.NextAction + " " + last : status.NextAction;
}

/// <summary>
/// This PC's status-view preferences. Kept apart from config.json, which every library operation fingerprints, so
/// switching notifications from the tray never makes a running finish or review see a changed configuration.
/// </summary>
internal sealed record DesktopPreferences(int Format, bool Notifications)
{
    public const string FileName = "desktop-preferences.json";
    public static readonly DesktopPreferences Default = new(1, true);

    /// <summary>The saved preferences, or the defaults when none are saved or the file is unreadable.</summary>
    public static DesktopPreferences Read(LocalState state)
    {
        try
        {
            var saved = Json.ReadFile<DesktopPreferences>(Path.Combine(state.Root, FileName), "Status view preferences");
            return saved.Format == 1 ? saved : Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return Default; }
    }

    public void Save(LocalState state)
    {
        Directory.CreateDirectory(state.Root);
        AtomicFile.Replace(Path.Combine(state.Root, FileName), Json.Serialize(this));
    }
}

using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Platform;

internal sealed record ProgressEnvelope(int Format, string LibraryId, string AccountId, string DeviceId,
    DateTimeOffset ConfirmedAt, SessionProgress Progress);

internal sealed record DesktopNotice(string Message, DateTimeOffset At);

internal sealed record DesktopStatus(SessionPhase Phase, string NextAction, DateTimeOffset? ConfirmedAt,
    int? QueuedFiles = null, long? QueuedBytes = null, string? Replica = null, bool CanRetry = false, string? Notice = null)
{
    public string Title => Phase switch
    {
        SessionPhase.Active => "Recording / Active",
        SessionPhase.Waiting => "Waiting for another PC",
        SessionPhase.RecoveryNeeded => "Recovery needed",
        SessionPhase.Unknown => "Unknown / unreachable",
        _ => Phase.ToString(),
    };

    public static DesktopStatus Unknown(DateTimeOffset? lastConfirmed = null) => new(SessionPhase.Unknown,
        "Status is stale or unreadable. Keep this PC's session and cache; reconnect to storage or reopen status to retry.", lastConfirmed);

    public DesktopStatus At(DateTimeOffset now) => ConfirmedAt is { } time && now - time <= TimeSpan.FromSeconds(20)
        && time <= now + TimeSpan.FromSeconds(5) ? this : Unknown(ConfirmedAt);

    internal static DesktopStatus Resolve(OwnershipRecord journal, DeviceIdentity device, ProgressEnvelope? report,
        bool supervisorRunning, DateTimeOffset now)
    {
        if (!Enum.IsDefined(journal.State) || journal.State != OwnershipState.Clean
            && (journal.Owner is null || journal.SessionId is null)) return Unknown();
        bool fresh = report is { Format: 1, Progress: not null } && report.LibraryId == journal.LibraryId
            && report.AccountId == journal.AccountId && report.DeviceId == device.DeviceId
            && report.ConfirmedAt <= now + TimeSpan.FromSeconds(5) && now - report.ConfirmedAt <= TimeSpan.FromSeconds(20);
        var progress = fresh ? report?.Progress : null;
        if (journal.Owner is { } owner && owner.DeviceId != device.DeviceId)
            return new(journal.State == OwnershipState.RecoveryRequired ? SessionPhase.RecoveryNeeded : SessionPhase.Waiting,
                $"Quit Medal and finish the handoff on {owner.DisplayName}. Reopen Medal here after that PC is Ready.", now);
        if (journal.State == OwnershipState.Clean)
        {
            // An operation may be preparing to acquire ownership. Never flash Ready during startup.
            if (supervisorRunning)
                return progress?.Phase == SessionPhase.Starting
                    ? new(SessionPhase.Starting, progress.NextAction, report!.ConfirmedAt) : Unknown();
            // The journal is re-read on every poll. Only CLEAN (committed and released) can be Ready.
            return new(SessionPhase.Ready, "Ready to switch PCs. You can open Medal on either PC.", now);
        }
        if (!supervisorRunning)
            return journal.State == OwnershipState.Starting
                ? new(SessionPhase.RecoveryNeeded, "Keep this PC's cache. Open Medal Sync on this PC to retry interrupted startup cleanup.", now)
                : new(SessionPhase.RecoveryNeeded, "Keep this PC's cache. Quit Medal normally, then retry finish on this PC.", now, CanRetry: true);
        if (progress is null || report is null || progress.SessionId != journal.SessionId) return Unknown(report?.ConfirmedAt);
        if (!Enum.IsDefined(progress.Phase) || progress.Phase is SessionPhase.Ready or SessionPhase.Waiting) return Unknown(report.ConfirmedAt);
        return new(progress.Phase, progress.NextAction, report.ConfirmedAt, progress.QueuedFiles, progress.QueuedBytes, progress.Replica);
    }
}

/// <summary>Private, replaceable local telemetry. Failure cannot interrupt supervision.</summary>
internal sealed class DesktopStatusPublisher : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private ProgressEnvelope _report;
    private readonly Timer _heartbeat;

    public DesktopStatusPublisher(LocalState state, string libraryId, string accountId, string deviceId)
    {
        _path = Path.Combine(state.Root, "desktop-status.json");
        _report = new(1, libraryId, accountId, deviceId, DateTimeOffset.UtcNow,
            new(SessionPhase.Starting, "Preparing the shared library. Keep Medal closed until startup finishes."));
        Write();
        _heartbeat = new Timer(_ => Write(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Report(SessionProgress progress)
    {
        lock (_gate)
        {
            _report = _report with { Progress = progress with { SessionId = progress.SessionId ?? _report.Progress.SessionId } };
            Write();
        }
    }

    private void Write()
    {
        lock (_gate)
        {
            try
            {
                _report = _report with { ConfirmedAt = DateTimeOffset.UtcNow };
                AtomicFile.Replace(_path, Json.Serialize(_report));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Advisory only. */ }
        }
    }

    public void Dispose() => _heartbeat.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

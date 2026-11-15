using System.Data.Common;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Session;

/// <summary>Supervision while Medal runs, then the finish it hands over to.</summary>
internal sealed partial class SessionCoordinator
{
    /// <summary>Watches a running session until Medal exits, then finishes it.</summary>
    public FinishOutcome Supervise(string session, Action<string> notify, CancellationToken cancellation)
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        int consecutiveStatusFailures = 0;
        int failedAttempts = 0;
        var nextCompatibilityCheck = clock.Now;
        var nextRequestCleanup = clock.Now;
        // The PCs asking for the library at the last successful check, shown in this PC's status while Medal runs.
        IReadOnlyList<string> waiting = [];
        void Send(string message)
        {
            // A notice is advisory. A failed log write or view must never end supervision while Medal runs.
            try { notify(message); }
            catch (Exception) { /* Dropped; the session continues. */ }
        }
        void Once(string message)
        {
            if (told.Add(message)) Send(message);
        }
        while (!cancellation.IsCancellationRequested)
        {
            if (shutdown?.Ending == true) return FinishShutdown(session);
            if (!medal.IsRunning())
            {
                var outcome = Finish(session);
                if (outcome.Status != FinishStatus.Pending) return outcome;
                if (outcome.Cause != PendingCause.None)
                {
                    // Bounded retries that never repeat the same notice. An outage is cheap to probe: 30 s doubling to
                    // 5 minutes. Any other failure repeats a full copy-and-verify pass, so it waits up to 10 minutes.
                    // Each attempt still verifies from scratch; backoff only changes how often one starts.
                    Once(outcome.StorageOffline ? "Waiting for storage. Clips are safe on this PC; the switch finishes automatically when storage is back." : outcome.Message);
                    int limit = outcome.StorageOffline ? 300 : 600;
                    var retryAt = clock.Now + TimeSpan.FromSeconds(Math.Min(limit, 30 << Math.Min(failedAttempts++, 5)));
                    // Short steps keep cancellation, shutdown and a reopened Medal responsive during the wait.
                    while (clock.Now < retryAt && !cancellation.IsCancellationRequested && shutdown?.Ending != true && !medal.IsRunning())
                        Pause(options.PollInterval);
                    continue;
                }
                failedAttempts = 0;
                Once(outcome.Message);
            }
            else
            {
                if (compatibility is not null && clock.Now >= nextCompatibilityCheck)
                {
                    nextCompatibilityCheck = clock.Now + TimeSpan.FromMinutes(1);
                    try
                    {
                        if (compatibility.CheckForUpdate() is { } warning) Once(warning);
                    }
                    catch (Exception ex) when (IsExpected(ex))
                    {
                        Once($"Medal compatibility could not be checked: {ex.Message} Keep this PC's session and cache; retry 'medal-sync compatibility --json'.");
                    }
                }
                try
                {
                    // A request left by this PC itself (for example from before it took the library) never asks it to switch.
                    var requests = store.PendingRequests(clock.Now).Where(r => r.Device.DeviceId != Me.DeviceId).ToList();
                    foreach (var request in requests)
                    {
                        // Keyed by request, so a new request from the same PC notifies again.
                        if (told.Add("request:" + request.RequestId)) Send($"{request.Device.DisplayName} wants to use Medal. {QuitFromTray} to switch.");
                    }
                    // A withdrawn request (the other PC cancelled, timed out or gave up) disappears from the status at once.
                    waiting = requests.Select(r => r.Device.DisplayName).Distinct(StringComparer.Ordinal).ToList();
                    if (clock.Now >= nextRequestCleanup)
                    {
                        nextRequestCleanup = clock.Now + TimeSpan.FromMinutes(10);
                        foreach (var stale in store.StaleRequests(clock.Now)) store.RemoveStaleRequest(stale);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Once("Clip storage is unreachable. Keep recording; uploads resume when it returns, and this PC keeps the library until then.");
                }
                if (space is not null)
                {
                    // Probes run in the background with a short wait, so a slow disk cannot hold up supervision.
                    try { foreach (string notice in space.Check(clock.Now)) Send(notice); }
                    catch (Exception) { /* Advisory only. */ }
                }
                var health = mount.Health();
                if (health.Healthy)
                {
                    consecutiveStatusFailures = 0;
                    // Also restores the recording limit if Medal reopened while uploads were finishing.
                    if (mount.UseUploadLimit(recording: true) is { } limitProblem) Once(limitProblem);
                    bool recording = IsRecording();
                    Report(SessionPhase.Active, ActiveStatus(recording, waiting), session, recording: recording);
                }
                else
                {
                    Report(health.Retryable ? SessionPhase.Unknown : SessionPhase.RecoveryNeeded,
                        "Clip drive status could not be confirmed. Keep this PC's session and cache; check storage before switching.", session);
                    // One timed-out status request during a large file read is
                    // not proof that the clip drive has failed. Confirm repeated
                    // unknown responses before interrupting the user. Wrong
                    // storage identity and other definite failures alert at once.
                    if (!health.Retryable || ++consecutiveStatusFailures >= 3) Once($"Clip drive problem: {health.Detail}");
                }
            }
            Pause(options.PollInterval);
        }
        return new FinishOutcome(FinishStatus.Pending, "Stopped watching; the session stays on this PC.", -1);
    }

    /// <summary>
    /// The live status while Medal runs. While other PCs are asking for the library it says who, instead of the
    /// general advice, so a notice replaced by a later one cannot hide that someone is waiting.
    /// </summary>
    internal static string ActiveStatus(bool recording, IReadOnlyList<string> waiting)
    {
        if (waiting.Count == 0) return recording ? RecordingMessage : ActiveMessage;
        string who = waiting.Count == 1 ? waiting[0] : string.Join(", ", waiting.SkipLast(1)) + " and " + waiting[^1];
        string asks = waiting.Count == 1 ? "is waiting" : "are waiting";
        return (recording ? "Medal is recording a game. " : "Medal is open. ")
            + $"{who} {asks} for the library. {QuitFromTray} when you are done here, and Medal Sync hands it off.";
    }

    private FinishOutcome FinishShutdown(string session)
    {
        try
        {
            // Windows requests Medal/recorder exit first. A hidden UI is never
            // proof that saving finished. Never kill or publish under a writer.
            while (medal.IsRunning())
            {
                shutdown!.RequireTime();
                Pause(TimeSpan.FromMilliseconds(250));
            }
            return Finish(session);
        }
        catch (OperationCanceledException)
        {
            return new FinishOutcome(FinishStatus.Pending,
                "Medal has not exited within the Windows shutdown budget; the session and cache are retained for next sign-in.", -1);
        }
    }
}

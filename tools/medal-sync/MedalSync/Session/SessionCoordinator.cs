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
internal sealed class SessionCoordinator(
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
    Func<LaunchPreflight>? preflight = null)
{
    private const string QuitFromTray = "Quit Medal from its tray icon";
    private const string ActiveMessage = "Medal is open. Quit Medal normally from its tray icon when you want to switch PCs.";
    private const string RecordingMessage = "Medal is recording a game. Quit Medal normally from its tray icon when you want to switch PCs.";
    private const string ShutdownMessage = "Windows is shutting down; Medal was not started.";
    private const string WaitCancelledMessage = "Stopped waiting for the other PC. Medal was not started and this PC's switch request was withdrawn.";
    private string? _uploadSession;
    private readonly Dictionary<ulong, (DateTimeOffset Since, long Tries)> _uploadRetries = [];

    // Set only while LaunchWhenReleased runs: its one switch request and the caller's cancellation.
    private HandoffRequest? _waitRequest;
    private bool _waitRequestSent;
    private Func<bool>? _waitCancelled;
    private DateTimeOffset? _waitDeadline;

    private DeviceIdentity Me => options.Device;

    public LaunchOutcome Launch()
    {
        Report(SessionPhase.Starting, "Preparing the shared library. Keep Medal closed until startup finishes.");
        var outcome = LaunchCore();
        ReportLaunch(outcome);
        return outcome;
    }

    /// <summary>
    /// Launch, except that while another PC owns the library this PC keeps one switch request and checks back, with
    /// backoff, until that PC releases it, <see cref="HandoffWait.Timeout"/> passes, the caller cancels or Windows
    /// shuts down. Release is only a Clean journal read from storage, never elapsed time, a missing lock or an
    /// unreachable owner. The normal atomic acquisition then decides between PCs that waited; the loser keeps waiting.
    /// Unless Medal started, this wait's own request is withdrawn and a newer one is left in place.
    /// </summary>
    public LaunchOutcome LaunchWhenReleased(HandoffWait wait)
    {
        Report(SessionPhase.Starting, "Preparing the shared library. Keep Medal closed until startup finishes.");
        var request = new HandoffRequest(HandoffRequest.CurrentFormat, Me, NewId(), clock.Now);
        (_waitRequest, _waitRequestSent, _waitCancelled) = (request, false, wait.Cancelled);
        LaunchOutcome outcome;
        bool sent;
        try
        {
            outcome = WaitForRelease(wait);
        }
        catch
        {
            if (_waitRequestSent) WithdrawWaitRequest(request);
            throw;
        }
        finally
        {
            sent = _waitRequestSent;
            (_waitRequest, _waitRequestSent, _waitCancelled, _waitDeadline) = (null, false, null, null);
        }
        if (sent && outcome.Status is not (LaunchStatus.Started or LaunchStatus.AlreadyRunning)) WithdrawWaitRequest(request);
        ReportLaunch(outcome);
        return outcome;
    }

    private LaunchOutcome WaitForRelease(HandoffWait wait)
    {
        var deadline = clock.Now + wait.Timeout;
        var interval = wait.FirstPoll;
        DeviceIdentity? owner = null;
        OwnershipState? state = null;
        DateTimeOffset? seenAt = null;
        HandoffWaitUpdate? last = null;
        _waitDeadline = deadline;
        while (!WaitStopped())
        {
            // The wait is bounded: no check, and so no launch, begins once the deadline has passed.
            if (last is not null && clock.Now >= deadline) return WaitTimedOut(wait, last);
            var outcome = LaunchCore();
            if (outcome.Wait == LaunchWait.Expired) return ReleasedTooLate(wait);
            if (outcome.Wait == LaunchWait.None) return outcome;
            bool fresh = outcome.Wait == LaunchWait.OwnedElsewhere;
            if (outcome.Journal is { } journal)
            {
                // A change such as in use to uploading usually comes shortly before release, so look again soon.
                if (seenAt is not null && (journal.State != state || journal.Owner != owner)) interval = wait.FirstPoll;
                (owner, state, seenAt) = (journal.Owner, journal.State, clock.Now);
            }
            else if (fresh)
            {
                // Another waiting PC acquired first; the next check reads who owns it now.
                (owner, state, seenAt, interval) = (null, null, clock.Now, wait.FirstPoll);
            }
            var update = new HandoffWaitUpdate(owner, state, seenAt, fresh, deadline - clock.Now);
            last = update;
            if (clock.Now >= deadline) return WaitTimedOut(wait, update);
            Report(fresh ? SessionPhase.Waiting : SessionPhase.WaitingForStorage, update.Describe());
            try { wait.Update?.Invoke(update); }
            catch (Exception) { /* The view is advisory; the wait continues. */ }
            var left = deadline - clock.Now;
            if (!Pause(interval < left ? interval : left, wait.Slice)) break;
            interval = interval + interval < wait.MaxPoll ? interval + interval : wait.MaxPoll;
        }
        return new LaunchOutcome(LaunchStatus.Waiting, shutdown?.SuppressUi == true ? ShutdownMessage : WaitCancelledMessage);
    }

    /// <summary>The library became free, but a slow check finished after the deadline; nothing was acquired.</summary>
    private static LaunchOutcome ReleasedTooLate(HandoffWait wait) => new(LaunchStatus.Waiting,
        $"Stopped waiting after {HandoffWaitUpdate.Duration(wait.Timeout)}: the other PC handed off the library just as the wait ended, "
        + "so Medal was not started. Open Medal again to start it.");

    private static LaunchOutcome WaitTimedOut(HandoffWait wait, HandoffWaitUpdate update)
    {
        string after = $"Stopped waiting after {HandoffWaitUpdate.Duration(wait.Timeout)}";
        if (!update.Fresh)
        {
            return new LaunchOutcome(LaunchStatus.Waiting,
                $"{after}: the shared library's server could not be reached. Nothing was changed; open Medal again once it is back.", StorageOffline: true);
        }
        string where = update.Owner?.DisplayName ?? "another PC";
        string what = update.State is { } state ? $" ({Ownership.Describe(state)})" : "";
        return new LaunchOutcome(LaunchStatus.Waiting,
            $"{after}: {where} still has the library{what}. Medal was not started and this PC's switch request was withdrawn; "
            + $"quit Medal on {where}, then open Medal again here.");
    }

    /// <summary>Sleeps in short slices so cancellation and shutdown stop the wait promptly. False when stopped.</summary>
    private bool Pause(TimeSpan duration, TimeSpan slice)
    {
        var until = clock.Now + duration;
        while (clock.Now < until)
        {
            if (WaitStopped()) return false;
            var left = until - clock.Now;
            clock.Sleep(left < slice ? left : slice);
        }
        return !WaitStopped();
    }

    private bool WaitStopped() => shutdown?.SuppressUi == true || WaitCancelled();

    private bool WaitCancelled()
    {
        try { return _waitCancelled?.Invoke() == true; }
        catch (Exception) { return true; } // Unsure whether the user still wants Medal: do not start it.
    }

    /// <summary>Removes only this wait's request; a newer request from this PC stays. Never throws.</summary>
    private void WithdrawWaitRequest(HandoffRequest request)
    {
        try { store.WithdrawRequest(request); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MedalSyncException)
        {
            // It expires after HandoffRequest.Lifetime and this PC's next session removes it.
        }
    }

    private void ReportLaunch(LaunchOutcome outcome)
    {
        Report(outcome.Status switch
        {
            LaunchStatus.Started or LaunchStatus.AlreadyRunning => SessionPhase.Active,
            LaunchStatus.Waiting when outcome.StorageOffline => SessionPhase.WaitingForStorage,
            LaunchStatus.Waiting => SessionPhase.Waiting,
            _ => SessionPhase.RecoveryNeeded,
        }, outcome.Status is LaunchStatus.Started or LaunchStatus.AlreadyRunning
            ? ActiveMessage + (outcome.Warning is { } warning ? " " + warning : "") : outcome.Message, outcome.Session);
    }

    private LaunchOutcome LaunchCore()
    {
        if (shutdown?.SuppressUi == true) return new LaunchOutcome(LaunchStatus.Waiting, ShutdownMessage);
        OwnershipRecord journal;
        try { journal = store.ReadJournal(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StorageOfflineLaunch();
        }
        if (Platform.PersistentMediaRefresh.IsPending(journal))
            return new LaunchOutcome(LaunchStatus.Blocked, "A persistent media refresh retains ownership. Retry its exact refresh-media review before opening Medal.", journal.SessionId);
        if (EnrollmentOwnership.IsPending(journal) && journal.Owner?.DeviceId == Me.DeviceId)
            return new LaunchOutcome(LaunchStatus.Blocked, EnrollmentOwnership.Interrupted(options.EnrollmentFolder), journal.SessionId);
        // Leftovers of published sessions only; anything still left is reported by the next finish.
        _ = RetryFinishedCleanup();
        if (medal.IsRunning())
        {
            if (journal.State == OwnershipState.Active && journal.Owner?.DeviceId == Me.DeviceId)
            {
                // Ownership survives a reboot or companion crash; it does not
                // prove that a manually reopened Medal still has its drive.
                // Never resume uploads or replace the library under that app.
                if (!mount.IsRunning)
                    return new LaunchOutcome(LaunchStatus.Blocked,
                        $"Medal is running but its clip drive is stopped. {QuitFromTray}, then open Medal Library to recover this PC's session.");
                var health = mount.Health();
                if (!health.Healthy)
                    return new LaunchOutcome(LaunchStatus.Blocked,
                        $"Medal's clip drive could not be verified: {health.Detail} {QuitFromTray}, then open Medal Library to recover this PC's session.");
                return new LaunchOutcome(LaunchStatus.AlreadyRunning, "Medal is already running in this PC's session.", journal.SessionId);
            }
            return new LaunchOutcome(LaunchStatus.Blocked,
                "Medal is already running without medal-sync (for example after an update or from its own startup entry). "
                + $"{QuitFromTray}, then open Medal again from the medal-sync shortcut.");
        }
        if (journal.Owner?.DeviceId == Me.DeviceId)
        {
            var previous = ResolveOwnSession(journal);
            if (previous is not null) return previous;
            journal = store.ReadJournal();
        }
        if (journal.State != OwnershipState.Clean)
        {
            string where = journal.Owner?.DisplayName ?? "another PC";
            if (journal.State == OwnershipState.RecoveryRequired)
            {
                return new LaunchOutcome(LaunchStatus.Blocked, $"Medal's library is waiting for recovery on {where}: {journal.Reason}");
            }
            if (!SendRequest()) return StorageOfflineLaunch();
            return new LaunchOutcome(LaunchStatus.Waiting,
                $"Medal is {Ownership.Describe(journal.State)} on {where}. A switch request was sent; quit Medal there and this PC can start.",
                Wait: LaunchWait.OwnedElsewhere, Journal: journal);
        }
        return StartSession();
    }

    private static LaunchOutcome StorageOfflineLaunch() => new(LaunchStatus.Waiting,
        "Waiting for storage: the shared library's server cannot be reached right now. Nothing was changed; open Medal again once it is back.",
        StorageOffline: true, Wait: LaunchWait.ServerUnreachable);

    /// <summary>
    /// A plain launch sends a new request every time. An explicit wait sends its one request once, and an outage
    /// while sending it is only a reason to keep waiting. False when that request could not be written.
    /// </summary>
    private bool SendRequest()
    {
        if (_waitRequest is null)
        {
            store.WriteRequest(new HandoffRequest(HandoffRequest.CurrentFormat, Me, NewId(), clock.Now));
            return true;
        }
        if (_waitRequestSent) return true;
        try
        {
            store.WriteRequest(_waitRequest);
            _waitRequestSent = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (StorageBusyException)
        {
            // Other PCs are updating cloud requests at the same moment; the journal was read, so retry on the next check.
            return true;
        }
    }

    /// <summary>Clears this PC's own unfinished session before a new one starts.</summary>
    private LaunchOutcome? ResolveOwnSession(OwnershipRecord journal)
    {
        string session = journal.SessionId!;
        if (journal.State == OwnershipState.Starting)
        {
            string cleanup = GiveBackStart(session, "restarted before Medal launched");
            return cleanup.Length == 0 ? null : new LaunchOutcome(LaunchStatus.Blocked, cleanup.TrimStart(), session);
        }
        var finished = Finish(session);
        return finished.Status is FinishStatus.Published or FinishStatus.Unchanged
            ? null
            : new LaunchOutcome(finished.Status == FinishStatus.Pending && finished.Cause != PendingCause.NeedsAction ? LaunchStatus.Waiting : LaunchStatus.Blocked,
                "This PC's previous session must finish first. " + finished.Message, StorageOffline: finished.StorageOffline);
    }

    private LaunchOutcome StartSession()
    {
        // Refuse an incompatible update before acquiring ownership, restoring
        // a database or starting storage. Interrupted sessions finish above.
        try { compatibility?.RequireLaunch(); }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new LaunchOutcome(LaunchStatus.Blocked, ex.Message);
        }
        // Read-only provider checks: a refusal here changes nothing, and the library stays Ready.
        var checkedStorage = preflight?.Invoke() ?? LaunchPreflight.Proceed;
        if (shutdown?.SuppressUi == true) return new LaunchOutcome(LaunchStatus.Waiting, ShutdownMessage);
        if (WaitCancelled()) return new LaunchOutcome(LaunchStatus.Waiting, WaitCancelledMessage);
        // The Clean read or the provider check may have outlasted an explicit wait; never acquire after its deadline.
        if (_waitDeadline is { } deadline && clock.Now >= deadline) return new LaunchOutcome(LaunchStatus.Waiting, "", Wait: LaunchWait.Expired);
        if (checkedStorage.Refusal is { } refusal) return new LaunchOutcome(LaunchStatus.Blocked, refusal);
        string session = NewId();
        OwnershipRecord journal;
        try
        {
            journal = store.Update(r => Ownership.Acquire(r, Me, session, clock.Now), options.LockTimeout);
        }
        catch (OwnershipConflictException ex)
        {
            // Another PC acquired between the Clean read and this update; a waiting PC keeps waiting.
            return new LaunchOutcome(LaunchStatus.Waiting, ex.Message, Wait: LaunchWait.OwnedElsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StorageOfflineLaunch();
        }
        RestoreResult restored;
        string? warning;
        Report(SessionPhase.Starting, "Restoring the committed library and preparing clip storage.", session);
        try
        {
            var generation = store.Generations.Read(journal.CommittedGeneration);
            // Checked again here: Medal can be opened while this PC finished an
            // earlier session or waited for the server.
            if (medal.IsRunning())
            {
                throw new UnsafeOperationException(
                    $"Medal was opened while the shared library was being prepared. {QuitFromTray}, then open Medal again from the medal-sync shortcut.");
            }
            RequireNoUnsharedChanges(generation);
            // The verified committed generation may intentionally contain no
            // clips. The history check above (or explicit replacement review)
            // authorizes replacing this PC's old entries, with a backup.
            // --replace-local-library skipped the history check, so this backup may hold the only copy of unshared entries.
            restored = library.Restore(generation.Snapshot, allowEmptyReplacement: true, keepBackup: options.ReplaceLocalLibrary);
            RequireCaptureFolder();
            warning = Join(checkedStorage.Warning, replication?.Prepare(generation.Snapshot));
            mount.Start(session, forRecording: true);
            var health = mount.Health();
            if (!health.Healthy) throw new UnsafeOperationException(health.Detail);
            var blocking = assets.ThroughMount(generation.Snapshot).Blocking.ToList();
            if (blocking.Count > 0)
            {
                throw new UnsafeOperationException(
                    $"{blocking.Count} file(s) the library needs are not usable, for example {blocking[0].Reference.Path} ({blocking[0].Problem}). "
                    + "Medal could remove them from the library, so it was not started.");
            }
            // Medal can otherwise replace the restored library and this PC's
            // settings with its unrelated full-database capture-folder backup.
            compatibility?.RequireLaunch();
            prepareNativeBackup?.Invoke();
            compatibility?.RequireLaunch();
            if (shutdown?.SuppressUi == true) throw new UnsafeOperationException(ShutdownMessage);
            // A wait cancelled while storage was being prepared must not open Medal later.
            if (WaitCancelled()) throw new OperationCanceledException(WaitCancelledMessage);
        }
        catch (Exception ex)
        {
            // Medal never ran, but ownership can only be given back after the
            // writable mount is verified stopped.
            string cleanup = GiveBackStart(session, ex.Message);
            if (ex is OperationCanceledException && shutdown?.Ending == true)
                return new LaunchOutcome(LaunchStatus.Waiting, "Windows shutdown interrupted storage preparation. This PC can retry at next sign-in." + cleanup);
            if (ex is OperationCanceledException && WaitCancelled()) return new LaunchOutcome(LaunchStatus.Waiting, WaitCancelledMessage + cleanup);
            if (ex is StorageOfflineException) return new LaunchOutcome(LaunchStatus.Waiting, "Waiting for storage: " + ex.Message + cleanup, StorageOffline: true);
            if (!IsExpected(ex)) throw;
            return new LaunchOutcome(LaunchStatus.Blocked, ex.Message + cleanup);
        }
        store.Update(r => Ownership.MarkActive(r, Me, session, clock.Now), options.LockTimeout);
        store.RemoveRequest(Me);
        // Both updates above can take a storage round trip, so a cancelled wait is checked again at the final decision.
        // Medal never ran: the normal finish releases the library, or keeps it here if storage is unavailable.
        bool cancelled = WaitCancelled();
        if (cancelled || (shutdown is not null && !shutdown.TryLaunch(medal.Launch)))
        {
            var finished = Finish(session);
            return new LaunchOutcome(LaunchStatus.Waiting, cancelled ? WaitCancelledMessage + " " + finished.Message : finished.Message, session);
        }
        if (shutdown is null) medal.Launch();
        string backup = !restored.Changed ? ""
            : options.ReplaceLocalLibrary ? $" This PC's previous library was backed up to {restored.BackupPath}. {KeptBackupNote}"
            : $" This PC's previous library was backed up to {restored.BackupPath}.";
        return new LaunchOutcome(LaunchStatus.Started, "Medal started with the shared library." + backup + (warning is null ? "" : " " + warning), session,
            Warning: warning);
    }

    internal const string KeptBackupNote = "That backup is never removed automatically; only a reviewed 'medal-sync retention' removes it.";

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : first + " " + second;

    private string GiveBackStart(string session, string reason)
    {
        try
        {
            if (!mount.Stop())
                throw new UnsafeOperationException("The clip drive did not stop; it may still be in use.");
            if (mount.IsRunning)
                throw new UnsafeOperationException("The clip drive is still running after shutdown.");
            store.Update(r =>
            {
                var aborted = Ownership.AbortStart(r, Me, session, reason, clock.Now);
                mount.RetainAbortedStart(session);
                return aborted;
            }, options.LockTimeout);
            return "";
        }
        catch (Exception cleanup)
        {
            return $" The session and cache stay on this PC ({cleanup.Message}); open Medal Library on this PC to retry cleanup.";
        }
    }

    /// <summary>
    /// The local library may be overwritten only when it holds nothing that was
    /// never shared: it is empty, or it equals a generation in the committed
    /// history. Anything else means Medal ran outside medal-sync (or this PC's
    /// session was abandoned), and replacing it would lose that work.
    /// </summary>
    private void RequireNoUnsharedChanges(GenerationContent target)
    {
        if (options.ReplaceLocalLibrary) return;
        var local = library.Read();
        string digest = local.Digest();
        if (digest == target.Manifest.SnapshotDigest) return;
        if (local.RowCount("contents") == 0 && local.RowCount("deleted_contents") == 0) return;
        bool pruned = false;
        foreach (var (_, manifest) in store.Generations.Chain(target.Manifest.Generation).Skip(1))
        {
            if (manifest is null) { pruned = true; continue; }
            if (manifest.SnapshotDigest == digest) return;
        }
        throw new UnsafeOperationException(
            "This PC's Medal library has changes that were never shared, usually because Medal was opened without medal-sync. "
            + (pruned ? "Older shared generations were removed by retention, so a library that matches one of them can no longer be recognized. " : "")
            + "Nothing was changed. Keep a copy with 'medal-sync snapshot export --out FILE', then run "
            + "'medal-sync launch --replace-local-library' to replace it with the shared library (a backup is kept until you remove it).");
    }

    private void RequireCaptureFolder()
    {
        string? folder = library.CaptureFolder();
        if (folder is null || !WindowsPath.TryParse(folder, out var path, out _) || !path.Equals(options.CaptureFolder))
        {
            throw new UnsafeOperationException(
                $"Medal records to '{folder ?? "(unset)"}', not {options.CaptureFolder.Display}. Set Medal's clip folder first.");
        }
    }

    /// <summary>Publishes the session after Medal has exited. Safe to call repeatedly.</summary>
    public FinishOutcome Finish(string session)
    {
        var outcome = FinishAttempt(session);
        Report(outcome.Status switch
        {
            FinishStatus.Published or FinishStatus.Unchanged => SessionPhase.Ready,
            FinishStatus.Pending when outcome.StorageOffline => SessionPhase.WaitingForStorage,
            FinishStatus.Pending when outcome.Cause == PendingCause.NeedsAction => SessionPhase.RecoveryNeeded,
            FinishStatus.Pending => SessionPhase.Unknown,
            _ => SessionPhase.RecoveryNeeded,
        }, outcome.Message, session, isOutcome: true);
        return outcome;
    }

    private FinishOutcome FinishAttempt(string session)
    {
        try
        {
            return FinishCore(session);
        }
        catch (OperationCanceledException) when (shutdown?.Ending == true)
        {
            return new FinishOutcome(FinishStatus.Pending, "Windows shutdown time limit reached; ownership and cached clips are retained for next sign-in.", -1);
        }
        catch (StorageOfflineException ex)
        {
            return new FinishOutcome(FinishStatus.Pending,
                $"Waiting for storage: {ex.Message} Clips are safe on this PC, which keeps the library; the switch finishes automatically when storage is back.",
                -1, PendingCause.StorageOffline);
        }
        catch (StorageActionRequiredException ex)
        {
            return new FinishOutcome(FinishStatus.Pending,
                $"Action needed before switching PCs: {ex.Message} The session and clips stay on this PC, and the switch is retried every few minutes.",
                -1, PendingCause.NeedsAction);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageBusyException)
        {
            if (ex is not StorageBusyException && !store.IsReachable())
                return new FinishOutcome(FinishStatus.Pending,
                    "Waiting for storage: the shared library's server cannot be reached right now. Clips are safe on this PC, which keeps the library; "
                    + "the switch finishes automatically when it is back.", -1, PendingCause.StorageOffline);
            return new FinishOutcome(FinishStatus.Pending,
                $"Clip storage is unreachable or busy ({ex.Message}); the session stays on this PC and will be retried.", -1,
                ex is StorageBusyException ? PendingCause.None : PendingCause.Failed);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new FinishOutcome(FinishStatus.Blocked, $"The session stays on this PC: {ex.Message}", -1);
        }
    }

    private FinishOutcome FinishCore(string session)
    {
        shutdown?.RequireTime();
        var journal = store.ReadJournal();
        if (Platform.PersistentMediaRefresh.IsPending(journal))
            return new FinishOutcome(FinishStatus.Blocked, "Retry the exact refresh-media review; its ownership and previous media are retained.", journal.CommittedGeneration);
        if (EnrollmentOwnership.IsPending(journal) && journal.Owner?.DeviceId == Me.DeviceId)
            return new FinishOutcome(FinishStatus.Blocked, EnrollmentOwnership.Interrupted(options.EnrollmentFolder), journal.CommittedGeneration);
        if (!journal.IsOwnedBy(Me, session))
        {
            return new FinishOutcome(FinishStatus.Blocked, $"This PC does not own session {session}.", journal.CommittedGeneration);
        }
        if (journal.State == OwnershipState.Starting)
        {
            if (medal.IsRunning()) return new FinishOutcome(FinishStatus.Blocked,
                "Quit Medal normally before recovering the interrupted start; ownership and cache are retained.", journal.CommittedGeneration);
            string cleanup = GiveBackStart(session, "explicit finish of interrupted start");
            return new FinishOutcome(cleanup.Length == 0 ? FinishStatus.Unchanged : FinishStatus.Blocked,
                cleanup.Length == 0 ? "Ready: interrupted start recovered. Login and unverified cache are retained as aborted-start evidence." : cleanup.TrimStart(),
                journal.CommittedGeneration);
        }
        if (Reopened(session) is { } running) return running;
        if (journal.State != OwnershipState.Publishing)
        {
            journal = store.Update(r => Ownership.BeginDrain(r, Me, session, clock.Now), options.LockTimeout);
            var drained = WaitForUploads(session);
            if (drained is not null) return drained with { Generation = journal.CommittedGeneration };
        }
        if (Reopened(session) is { } reopened) return reopened;
        // Uploads are allowed to drain even after an incompatible update, but
        // never publish that build's library or release its recovery cache.
        var publishVersions = compatibility?.RequirePublish() ?? options.Medal;
        Report(SessionPhase.Verifying, "Verifying library changes, required replicas and server files. Keep this PC on.", session);
        // Without this, an unreachable server would make every file look
        // deleted on purpose and hide the damage the audit looks for.
        shutdown?.RequireTime();
        assets.RequireServer();
        shutdown?.RequireTime();
        var snapshot = library.Read();
        var start = store.Generations.Read(journal.CommittedGeneration);
        var lost = LostEntries(start.Snapshot, snapshot);
        if (lost.Count > 0 && !options.AcceptLibraryChanges)
        {
            if (shutdown?.SuppressUi == true)
                return new FinishOutcome(FinishStatus.Blocked,
                    "Removed clips need review at next sign-in. Ownership and cache are retained; no changes were accepted during shutdown.",
                    journal.CommittedGeneration);
            if (reviewLibraryChanges is not null)
            {
                var review = new LibraryChangeReview(session, journal.CommittedGeneration, start.Snapshot, snapshot,
                    MissingMediaAudit.Compare(start.Snapshot, snapshot).Suspect.ToArray());
                if (!reviewLibraryChanges(review))
                    return new FinishOutcome(FinishStatus.Blocked,
                        "Library changes were not accepted. The session and cached files are retained. Open Medal Library to review again.",
                        journal.CommittedGeneration);
                if (medal.IsRunning() || store.ReadJournal() != journal || library.Read().Digest() != snapshot.Digest())
                    return new FinishOutcome(FinishStatus.Blocked,
                        "The session or library changed during review. Nothing was published; open Medal Library to review again.",
                        journal.CommittedGeneration);
                // Recheck reachability after a potentially long user interaction.
                assets.RequireServer();
            }
            else
            {
                return new FinishOutcome(FinishStatus.Blocked,
                    $"Medal removed or cleared {lost.Count} clip(s) whose files are still on the server (for example {lost[0]}). "
                    + "This usually follows a storage interruption. Review with 'medal-sync audit' and fix with 'medal-sync repair' "
                    + "before switching.", journal.CommittedGeneration);
            }
        }
        shutdown?.RequireTime();
        var replicated = replication?.Publish(snapshot) ?? ReplicationResult.None;
        Report(SessionPhase.Verifying, "Verifying server files, stopping the clip drive and committing the library. Wait for Ready.", session, replica: "Primary storage");
        if (Reopened(session) is { } duringReplication) return duringReplication;
        if (library.Read().Digest() != snapshot.Digest())
            return new FinishOutcome(FinishStatus.Blocked, "The library changed during replication; retry finish on this PC.", journal.CommittedGeneration);
        var onServer = assets.OnServer(snapshot);
        shutdown?.RequireTime();
        var missing = onServer.Blocking.ToList();
        if (missing.Count > 0)
        {
            return new FinishOutcome(FinishStatus.Blocked,
                $"{missing.Count} file(s) the library needs are not on the server, for example {missing[0].Reference.Path} ({missing[0].Problem}).",
                journal.CommittedGeneration);
        }
        if ((mount.IsRunning && !mount.Stop()) || mount.IsRunning)
        {
            return new FinishOutcome(FinishStatus.Blocked, "The clip drive did not stop; it may still be in use.", journal.CommittedGeneration);
        }
        // Once more after the drive stopped: the snapshot above must still be
        // the library Medal left behind when it is published.
        if (Reopened(session) is { } late) return late;
        if (compatibility is not null && compatibility.RequirePublish() != publishVersions)
            throw new IncompatibleLibraryException("Medal updated while the handoff was being verified. Retry finish on this PC; ownership and cache are retained.");
        shutdown?.RequireTime();
        if (library.Read().Digest() != snapshot.Digest())
            return new FinishOutcome(FinishStatus.Blocked,
                "The library changed during final verification; ownership and cache are retained. Retry finish on this PC.", journal.CommittedGeneration);
        journal = store.Update(r =>
        {
            shutdown?.RequireTime();
            return Ownership.BeginPublish(r, Me, session, clock.Now);
        }, options.LockTimeout);
        if (start.Manifest.SnapshotDigest == snapshot.Digest())
        {
            shutdown?.RequireTime();
            store.Update(r =>
            {
                shutdown?.RequireTime();
                return Ownership.CompleteUnchanged(r, Me, session, clock.Now);
            }, options.LockTimeout);
            return new FinishOutcome(FinishStatus.Unchanged, "Ready to switch: the library did not change." + Deferred(replicated) + Forget(session, journal.CommittedGeneration), journal.CommittedGeneration);
        }
        var published = Publish(journal, session, snapshot, onServer, publishVersions);
        return published with { Message = published.Message + Deferred(replicated) };
    }

    /// <summary>
    /// Medal opened again (Start menu, a medal:// link, its updater) before the
    /// switch finished. While the drive is still up the session simply carries
    /// on; once the drive has stopped the user must quit Medal first.
    /// </summary>
    private FinishOutcome? Reopened(string session)
    {
        if (!medal.IsRunning()) return null;
        var journal = store.ReadJournal();
        if (journal.State == OwnershipState.Draining && mount.IsRunning)
        {
            journal = store.Update(r => Ownership.ResumeActive(r, Me, session, clock.Now), options.LockTimeout);
        }
        return journal.State == OwnershipState.Active
            ? new FinishOutcome(FinishStatus.Pending, $"Medal is running, so the session stays on this PC. {QuitFromTray} to switch PCs.",
                journal.CommittedGeneration)
            : new FinishOutcome(FinishStatus.Blocked,
                $"Medal was opened while the switch was finishing. {QuitFromTray}, then run 'medal-sync finish'.", journal.CommittedGeneration);
    }

    private FinishOutcome Publish(OwnershipRecord journal, string session, LibrarySnapshot snapshot, AssetReport onServer, MedalVersions versions)
    {
        long committed = journal.CommittedGeneration;
        string digest = snapshot.Digest();
        // Folders above the committed generation are either this session's own
        // publish from before a crash (reused) or left by an abandoned session
        // (skipped: never committed, never read).
        var later = store.Generations.Numbered().Where(g => g > committed).ToList();
        long generation = later.FirstOrDefault(g => IsThisAttempt(g, committed, session, digest, versions), -1L);
        if (generation < 0)
        {
            // Also above the retention high-water mark, so a pruned attempt's number is never reused.
            generation = store.Generations.Next(committed);
            store.Generations.Publish(generation, committed, session, Me, snapshot, AssetManifest.From(onServer), versions, clock.Now);
        }
        shutdown?.RequireTime();
        store.Update(r =>
        {
            shutdown?.RequireTime();
            return Ownership.CompletePublish(r, Me, session, generation, clock.Now);
        }, options.LockTimeout);
        return new FinishOutcome(FinishStatus.Published, $"Ready to switch: generation {generation} published." + Forget(session, generation), generation);
    }

    private bool IsThisAttempt(long generation, long parent, string session, string digest, MedalVersions versions)
    {
        try
        {
            var manifest = store.Generations.Read(generation).Manifest;
            return manifest.SessionId == session && manifest.ParentGeneration == parent && manifest.SnapshotDigest == digest && manifest.Medal == versions;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string Deferred(ReplicationResult result) => result.Summary + (result.Deferred.Count == 0 ? ""
        : $" Cloud copy {string.Join(", ", result.Deferred.Select(n => $"'{n}'"))} is offline or failing; primary storage is verified and the copy retries on the next finish.");

    /// <summary>
    /// Removes the finished session's cache and login, and retries any an earlier finish left behind. A failure only
    /// leaves files behind: they are recorded as published, so they block nothing and the next launch or finish retries.
    /// </summary>
    private string Forget(string session, long generation)
    {
        string result = "";
        try
        {
            mount.Forget(session, generation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result = $" The finished session's SSD cache could not be removed yet ({ex.Message}). It is retried automatically and does not block setup.";
        }
        var left = RetryFinishedCleanup();
        return left.Count == 0 ? result : result + $" {left.Count} earlier finished session cache(s) could not be removed yet: {left[0]}";
    }

    private IReadOnlyList<string> RetryFinishedCleanup()
    {
        try { return mount.CleanupFinished(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return [ex.Message]; }
    }

    /// <summary>Null when drained; otherwise the reason to stop for now.</summary>
    private FinishOutcome? WaitForUploads(string session)
    {
        shutdown?.RequireTime();
        if (_uploadSession != session || !mount.IsRunning)
        {
            _uploadSession = session;
            _uploadRetries.Clear(); // Queue IDs belong to one mount process only.
        }
        if (!mount.IsRunning)
        {
            // After a restart the session's cache may still hold unsent files;
            // the same cache folder resumes them.
            mount.Start(session, forRecording: false);
        }
        // Medal has exited, so uploads no longer compete with the game. A failed switch only changes speed.
        string limitNotice = mount.UseUploadLimit(recording: false) is { } problem ? " " + problem : "";
        var deadline = clock.Now + options.DrainTimeout;
        while (true)
        {
            shutdown?.RequireTime();
            if (Reopened(session) is { } reopened) return reopened;
            var verdict = mount.Drain();
            bool needsAction = UploadRetriesNeedAction(verdict);
            Report(verdict.State switch
            {
                DrainState.Unknown => SessionPhase.Unknown,
                DrainState.Failed => SessionPhase.RecoveryNeeded,
                _ => SessionPhase.Uploading,
            }, verdict.State == DrainState.Unknown
                ? "Upload status is unknown. Keep this PC on; ownership and cache are retained."
                : "Finishing uploads to primary storage. Keep this PC on and wait for verification. " + string.Join(" ", verdict.Reasons) + limitNotice, session,
                verdict.State == DrainState.Unknown ? null : verdict.QueuedFiles,
                verdict.State == DrainState.Unknown ? null : verdict.QueuedBytes, "Primary storage");
            shutdown?.RequireTime();
            switch (verdict.State)
            {
                case DrainState.Drained:
                    return null;
                case DrainState.Failed:
                    return new FinishOutcome(FinishStatus.Blocked, "Uploads are failing: " + string.Join(" ", verdict.Reasons), 0);
            }
            if (needsAction)
                return new FinishOutcome(FinishStatus.Pending,
                    "Action needed: upload retries have not recovered for at least 15 minutes. " + string.Join(" ", verdict.Reasons)
                    + " Ownership and cached clips stay on this PC; finish will retry automatically.", 0, PendingCause.NeedsAction);
            if (clock.Now >= deadline)
            {
                return new FinishOutcome(FinishStatus.Pending, (verdict.CacheSpaceTrouble ? "Waiting for SSD cache cleanup: " : "Still uploading: ")
                    + string.Join(" ", verdict.Reasons), 0);
            }
            Pause(options.PollInterval);
        }
    }

    private bool UploadRetriesNeedAction(DrainVerdict verdict)
    {
        if (verdict.State == DrainState.Unknown) return false;
        var present = verdict.Uploads.Select(u => u.Id).ToHashSet();
        foreach (ulong id in _uploadRetries.Keys.Where(id => !present.Contains(id)).ToArray()) _uploadRetries.Remove(id);
        bool needsAction = false;
        foreach (var upload in verdict.Uploads)
        {
            if (_uploadRetries.TryGetValue(upload.Id, out var previous) && upload.Tries < previous.Tries)
                _uploadRetries.Remove(upload.Id);
            if (upload.Tries == 0) { _uploadRetries.Remove(upload.Id); continue; }
            if (upload.Uploading) continue; // A running attempt is not evidence that it failed.
            if (!_uploadRetries.TryGetValue(upload.Id, out var retry)) retry = (clock.Now, upload.Tries);
            _uploadRetries[upload.Id] = (retry.Since, upload.Tries);
            if (upload.Tries > 1 && clock.Now - retry.Since >= TimeSpan.FromMinutes(15)) needsAction = true;
        }
        return needsAction;
    }

    private void Pause(TimeSpan duration)
    {
        if (shutdown is null) clock.Sleep(duration);
        else shutdown.Pause(duration);
    }

    /// <summary>Library entries the session lost although their files still exist on the server.</summary>
    private List<string> LostEntries(LibrarySnapshot start, LibrarySnapshot now)
    {
        var contents = start.Table("contents");
        int id = contents.ColumnIndex("local_content_id");
        var rows = contents.Rows.ToDictionary(r => TableSnapshot.Text(r[id])!, StringComparer.Ordinal);
        var lost = new List<string>();
        foreach (var change in MissingMediaAudit.Compare(start, now).Suspect)
        {
            IEnumerable<string?> paths = change.Kind == MediaChangeKind.PathCleared
                ? [change.Before]
                : AssetInventory.PathColumns.Select(c => TableSnapshot.Text(rows[change.ContentId][contents.ColumnIndex(c)]));
            if (paths.Any(p => p is not null && assets.ExistsOnServer(p))) lost.Add(change.Before ?? change.ContentId);
        }
        return lost;
    }

    /// <summary>Watches a running session until Medal exits, then finishes it.</summary>
    public FinishOutcome Supervise(string session, Action<string> notify, CancellationToken cancellation)
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        int consecutiveStatusFailures = 0;
        int failedAttempts = 0;
        var nextCompatibilityCheck = clock.Now;
        var nextRequestCleanup = clock.Now;
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
                    foreach (var request in store.PendingRequests(clock.Now))
                    {
                        // Keyed by request, so a new request from the same PC notifies again.
                        if (told.Add("request:" + request.RequestId)) Send($"{request.Device.DisplayName} wants to use Medal. {QuitFromTray} to switch.");
                    }
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
                    Report(SessionPhase.Active, recording ? RecordingMessage : ActiveMessage, session, recording: recording);
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

    private static bool IsExpected(Exception ex) =>
        ex is MedalSyncException or IOException or InvalidDataException or UnauthorizedAccessException or DbException;

    /// <summary>Whether Medal is recording a game. Only changes the status shown, so a failed check means "open".</summary>
    private bool IsRecording()
    {
        try { return medal.IsRecording(); }
        catch (Exception) { return false; }
    }

    private void Report(SessionPhase phase, string action, string? session = null, int? files = null, long? bytes = null, string? replica = null,
        bool isOutcome = false, bool recording = false)
    {
        // A disconnected or failed view must never change ownership, uploads or recovery.
        try { progress?.Invoke(new(phase, action, session, files, bytes, replica, isOutcome, recording)); }
        catch (Exception) { /* Telemetry is advisory. */ }
    }

    internal static string NewId() => Guid.NewGuid().ToString("N");
}

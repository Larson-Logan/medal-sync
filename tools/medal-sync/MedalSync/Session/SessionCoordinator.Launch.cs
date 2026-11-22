using System.Data.Common;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Session;

/// <summary>Launch: wait for the library, take ownership, prepare storage and start Medal.</summary>
internal sealed partial class SessionCoordinator
{
    public LaunchOutcome Launch(bool atSignIn = false)
    {
        Report(SessionPhase.Starting, "Preparing the shared library. Keep Medal closed until startup finishes.");
        var outcome = LaunchCore(atSignIn);
        ReportLaunch(outcome);
        return outcome;
    }

    internal static bool CanUseLastSync(OwnershipRecord journal) => journal.State is
        OwnershipState.Starting or OwnershipState.Active or OwnershipState.Draining or OwnershipState.Publishing
        && journal.Owner is not null && journal.SessionId is not null
        && !Platform.PersistentMediaRefresh.IsPending(journal) && !EnrollmentOwnership.IsPending(journal);

    /// <summary>Use the reviewed generation after the user confirms the previous PC is powered off.</summary>
    public LaunchOutcome UseLastSync(OwnershipRecord reviewed)
    {
        if (shutdown?.SuppressUi == true) return new LaunchOutcome(LaunchStatus.Waiting, ShutdownMessage);
        if (!CanUseLastSync(reviewed) || reviewed.Owner!.DeviceId == Me.DeviceId || medal.IsRunning())
            return new LaunchOutcome(LaunchStatus.Blocked, "Open Medal Library again to check who owns the library. The handoff was canceled.");
        _ = store.Generations.Read(reviewed.CommittedGeneration);
        try
        {
            store.Update(current =>
            {
                if (current != reviewed || shutdown?.SuppressUi == true || medal.IsRunning())
                    throw new OwnershipConflictException("The handoff changed during confirmation. Open Medal Library to check the latest status.");
                // Exact journal match protects a newer checkpoint or session. This leaves the original PC's work intact.
                return Ownership.Abandon(current, Me, reviewed.SessionId!, "user confirmed the other PC was powered off", clock.Now);
            }, options.LockTimeout);
        }
        catch (OwnershipConflictException ex) { return new LaunchOutcome(LaunchStatus.Blocked, ex.Message); }
        return Launch();
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
        var previous = _sentRequest;
        bool reuse = previous is not null && previous.RequestedAt <= clock.Now && clock.Now - previous.RequestedAt < HandoffWait.ReuseRequest;
        var request = reuse ? previous! : new HandoffRequest(HandoffRequest.CurrentFormat, Me, NewId(), clock.Now);
        _sentRequest = null;
        (_waitRequest, _waitRequestSent, _waitCancelled) = (request, reuse, wait.Cancelled);
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
        DateTimeOffset? quietSince = null;
        string? maintenanceCommand = null;
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
                if (seenAt is not null && (journal.State != state || journal.Owner?.DeviceId != owner?.DeviceId)) interval = wait.FirstPoll;
                (owner, state, seenAt, quietSince) = (journal.Owner, journal.State, clock.Now, Ownership.QuietSince(journal, clock.Now));
                maintenanceCommand = journal.State == OwnershipState.Maintenance ? journal.Reason : null;
            }
            else if (fresh)
            {
                // Another waiting PC acquired first; the next check reads who owns it now.
                (owner, state, seenAt, quietSince, interval) = (null, null, clock.Now, null, wait.FirstPoll);
            }
            var update = new HandoffWaitUpdate(owner, state, seenAt, fresh, deadline - clock.Now, quietSince, maintenanceCommand);
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

    private LaunchOutcome WaitTimedOut(HandoffWait wait, HandoffWaitUpdate update)
    {
        string after = $"Stopped waiting after {HandoffWaitUpdate.Duration(wait.Timeout)}";
        if (!update.Fresh)
        {
            return new LaunchOutcome(LaunchStatus.Waiting,
                $"{after}: the shared library's server could not be reached. Nothing was changed; open Medal again once it is back.", StorageOffline: true);
        }
        string where = update.Owner?.DisplayName ?? "another PC";
        string what = update.State is { } state ? $" ({Ownership.Describe(state)})" : "";
        string request = _waitRequestSent ? ". Medal Sync will try to withdraw this PC's switch request" : ", and this PC could not confirm a switch request";
        return new LaunchOutcome(LaunchStatus.Waiting,
            $"{after}: {where} still has the library{what}. Medal was not started{request}. "
            + $"{Ownership.HandoffAdvice(update.State)} Open Medal again here once the library is Ready.");
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
        // Finish already reported the upload or storage state. The supervisor keeps it current.
        if (outcome.Status == LaunchStatus.FinishingPreviousSession) return;
        Report(outcome.Status switch
        {
            LaunchStatus.Started or LaunchStatus.AlreadyRunning => SessionPhase.Active,
            LaunchStatus.RecoveredPreviousSession => SessionPhase.Ready,
            LaunchStatus.Waiting when outcome.StorageOffline => SessionPhase.WaitingForStorage,
            LaunchStatus.Waiting => SessionPhase.Waiting,
            _ => SessionPhase.RecoveryNeeded,
        }, outcome.Status is LaunchStatus.Started or LaunchStatus.AlreadyRunning
            ? ActiveMessage + (outcome.Warning is { } warning ? " " + warning : "") : outcome.Message, outcome.Session);
    }

    private LaunchOutcome LaunchCore(bool atSignIn = false)
    {
        if (shutdown?.SuppressUi == true) return new LaunchOutcome(LaunchStatus.Waiting, ShutdownMessage);
        OwnershipRecord journal;
        try { journal = store.ReadJournal(); }
        catch (StorageActionRequiredException ex) { return StorageActionLaunch(ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StorageOfflineLaunch(ex);
        }
        if (journal.State == OwnershipState.Migrated)
            return new LaunchOutcome(LaunchStatus.Blocked, Ownership.MigratedMessage);
        if (journal.State == OwnershipState.Migrating)
            return new LaunchOutcome(LaunchStatus.Blocked, $"{journal.Owner?.DisplayName} is migrating the library to Google Drive. Retry after the migration completes.");
        if (journal.State == OwnershipState.Maintenance)
            return new LaunchOutcome(LaunchStatus.Waiting, $"{journal.Owner?.DisplayName} is running '{journal.Reason}'. Medal can open once maintenance releases the library.",
                Wait: LaunchWait.OwnedElsewhere, Journal: journal);
        if (Platform.PersistentMediaRefresh.IsPending(journal))
            return new LaunchOutcome(LaunchStatus.Blocked, "A persistent media refresh retains ownership. Retry its exact refresh-media review before opening Medal.", journal.SessionId);
        if (EnrollmentOwnership.IsPending(journal) && journal.Owner?.DeviceId == Me.DeviceId)
            return new LaunchOutcome(LaunchStatus.Blocked, EnrollmentOwnership.Interrupted(options.EnrollmentFolder), journal.SessionId);
        // Leftovers of published sessions only; anything still left is reported by the next finish.
        _ = RetryFinishedCleanup();
        if (medal.IsRunning())
            return CheckRunningSession(journal);
        if (journal.Owner?.DeviceId == Me.DeviceId)
        {
            var previous = ResolveOwnSession(journal);
            if (previous is not null) return previous;
            // A PC that waited through this PC's interrupted shutdown gets the library; otherwise sign-in starts Medal as usual.
            if (atSignIn && AnotherPcIsWaiting())
                return new LaunchOutcome(LaunchStatus.RecoveredPreviousSession, "This PC recovered and released its previous session.");
            try { journal = store.ReadJournal(); }
            catch (StorageActionRequiredException ex) { return StorageActionLaunch(ex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return StorageOfflineLaunch(ex); }
        }
        if (journal.State != OwnershipState.Clean)
        {
            string where = journal.Owner?.DisplayName ?? "another PC";
            if (journal.State == OwnershipState.RecoveryRequired)
            {
                return new LaunchOutcome(LaunchStatus.Blocked, $"Medal's library is waiting for recovery on {where}: {journal.Reason}");
            }
            bool shouldAsk = _waitRequest is not null || options.AskOwnerToSwitch;
            bool asked;
            try { asked = shouldAsk && SendRequest(); }
            catch (StorageActionRequiredException ex) { return StorageActionLaunch(ex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new LaunchOutcome(LaunchStatus.Waiting,
                    "Waiting for storage: this PC could not confirm the switch request. Open Medal again once storage is back.",
                    StorageOffline: true, Wait: LaunchWait.ServerUnreachable);
            }
            string message = shouldAsk && !asked && !Ownership.IsFinishing(journal.State)
                ? $"{where} still owns the library ({Ownership.Describe(journal.State)}). This PC could not send the switch request because storage is busy. "
                    + Ownership.HandoffAdvice(journal.State)
                : OwnedElsewhereMessage(journal.State, where, asked, Ownership.QuietSince(journal, clock.Now));
            return new LaunchOutcome(LaunchStatus.Waiting, message,
                Wait: LaunchWait.OwnedElsewhere, Journal: journal);
        }
        return StartSession();
    }

    /// <summary>
    /// Whether another PC asked for the library. A request this PC left itself does not count. When the requests
    /// cannot be read, sign-in keeps the library released rather than risk taking it from a waiting PC.
    /// </summary>
    private bool AnotherPcIsWaiting()
    {
        try { return store.PendingRequests(clock.Now).Any(r => r.Device.DeviceId != Me.DeviceId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private LaunchOutcome CheckRunningSession(OwnershipRecord journal, string? expectedSession = null)
    {
        if (journal.State != OwnershipState.Active || journal.Owner?.DeviceId != Me.DeviceId
            || expectedSession is not null && !journal.IsOwnedBy(Me, expectedSession))
            return new LaunchOutcome(LaunchStatus.Blocked,
                "Medal is already running without medal-sync (for example after an update or from its own startup entry). "
                + $"{QuitFromTray}, then open Medal again from the medal-sync shortcut.");
        // A manually reopened Medal still needs a running, verified clip drive.
        if (!mount.IsRunning)
            return new LaunchOutcome(LaunchStatus.Blocked,
                $"Medal is running but its clip drive is stopped. {QuitFromTray}, then open Medal Library to recover this PC's session.");
        var health = mount.Health();
        if (!health.Healthy)
            return new LaunchOutcome(LaunchStatus.Blocked,
                $"Medal's clip drive could not be verified: {health.Detail} {QuitFromTray}, then open Medal Library to recover this PC's session.");
        return new LaunchOutcome(LaunchStatus.AlreadyRunning, "Medal is already running in this PC's session.", journal.SessionId);
    }

    private LaunchOutcome CheckReopenedSession(string session)
    {
        try { return CheckRunningSession(store.ReadJournal(), session); }
        catch (StorageActionRequiredException ex) { return StorageActionLaunch(ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return StorageOfflineLaunch(ex); }
    }

    /// <summary>
    /// Why Medal did not start here, matched to what the owner is doing: a PC that is only uploading hands off by
    /// itself, so nobody needs to quit Medal there, and a launch that sent no request must not claim it did.
    /// </summary>
    internal static string OwnedElsewhereMessage(OwnershipState state, string where, bool asked, DateTimeOffset? quietSince = null)
    {
        if (quietSince is { } since) return Ownership.QuietFinishMessage(where, since);
        if (Ownership.IsFinishing(state))
        {
            return $"{where} still owns its last session ({Ownership.Describe(state)}). {Ownership.HandoffAdvice(state)} "
                + "Open Medal here once the library is Ready.";
        }
        string request = asked ? "A switch request was sent." : "This PC is waiting for the library to be released.";
        return $"{where} still owns the library ({Ownership.Describe(state)}). {request} {Ownership.HandoffAdvice(state)}";
    }

    /// <summary>An unreachable server, or one returning a stale journal: either way nothing was acquired.</summary>
    private static LaunchOutcome StorageOfflineLaunch(Exception? cause = null) => new(LaunchStatus.Waiting,
        cause is StaleCoordinationReadException stale
            ? "Waiting for storage: " + stale.Message + " Open Medal again after that."
            : "Waiting for storage: the shared library's server cannot be reached right now. Nothing was changed; open Medal again once it is back.",
        StorageOffline: true, Wait: LaunchWait.ServerUnreachable);

    private static LaunchOutcome StorageActionLaunch(StorageActionRequiredException cause) => new(LaunchStatus.Blocked,
        $"Action needed before opening Medal: {cause.Message}");

    /// <summary>
    /// A plain launch sends a new request every time. An explicit wait reuses its request once it has been sent.
    /// A busy writer leaves the request unsent, so an explicit wait tries again on the next check.
    /// </summary>
    private bool SendRequest()
    {
        if (_waitRequestSent) return true;
        var request = _waitRequest ?? new HandoffRequest(HandoffRequest.CurrentFormat, Me, NewId(), clock.Now);
        try
        {
            store.WriteRequest(request);
        }
        catch (StorageBusyException) { return false; }
        if (_waitRequest is null) _sentRequest = request;
        else _waitRequestSent = true;
        return true;
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
        if (finished.Status is FinishStatus.Published or FinishStatus.Unchanged) return null;
        string message = "This PC's previous session must finish first. " + finished.Message;
        if (finished.Status != FinishStatus.Pending || finished.Cause == PendingCause.NeedsAction)
            return new LaunchOutcome(LaunchStatus.Blocked, message, session);
        if (shutdown?.Ending == true) return new LaunchOutcome(LaunchStatus.Waiting, message, session);
        if (medal.IsRunning())
        {
            var reopened = CheckReopenedSession(session);
            if (reopened.Status != LaunchStatus.Waiting) return reopened;
        }
        return new LaunchOutcome(LaunchStatus.FinishingPreviousSession, message, session,
            StorageOffline: finished.StorageOffline, PendingFinish: finished);
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
        catch (StorageActionRequiredException ex) { return StorageActionLaunch(ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StorageOfflineLaunch(ex);
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
            // Inside the guard: a storage failure here gives the start back now, instead of leaving the drive mounted
            // with nothing supervising it. If the write landed but its read-back failed, cleanup keeps the session here.
            store.Update(r => Ownership.MarkActive(r, Me, session, clock.Now), options.LockTimeout);
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
        // Housekeeping only: an unremoved request expires by itself and a later session removes it, so it never stops Medal.
        try { store.RemoveRequest(Me); }
        catch (Exception ex) when (IsExpected(ex)) { }
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
        GenerationRetention? retention = null;
        foreach (var (number, manifest) in store.Generations.Chain(target.Manifest.Generation).Skip(1))
        {
            if (manifest is null)
            {
                if (store.IsCloud)
                {
                    retention ??= store.Generations.ReadRetention();
                    if (retention?.Find(number) is { Committed: true, SnapshotDigest: { } proof })
                    {
                        if (proof == digest) return;
                        continue;
                    }
                }
                pruned = true;
                continue;
            }
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
}

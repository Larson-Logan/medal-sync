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

internal sealed record LaunchOutcome(LaunchStatus Status, string Message, string? Session = null);

internal enum FinishStatus
{
    Published,
    Unchanged,
    Pending,
    Blocked,
}

internal sealed record FinishOutcome(FinishStatus Status, string Message, long Generation);

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
    Action<SessionProgress>? progress = null)
{
    private const string QuitFromTray = "Quit Medal from its tray icon";

    private DeviceIdentity Me => options.Device;

    public LaunchOutcome Launch()
    {
        Report(SessionPhase.Starting, "Preparing the shared library. Keep Medal closed until startup finishes.");
        var outcome = LaunchCore();
        Report(outcome.Status switch
        {
            LaunchStatus.Started or LaunchStatus.AlreadyRunning => SessionPhase.Active,
            LaunchStatus.Waiting => SessionPhase.Waiting,
            _ => SessionPhase.RecoveryNeeded,
        }, outcome.Status is LaunchStatus.Started or LaunchStatus.AlreadyRunning
            ? "Medal is active. Quit Medal normally from its tray icon when you want to switch PCs." : outcome.Message, outcome.Session);
        return outcome;
    }

    private LaunchOutcome LaunchCore()
    {
        if (shutdown?.SuppressUi == true) return new LaunchOutcome(LaunchStatus.Waiting, "Windows is shutting down; Medal was not started.");
        var journal = store.ReadJournal();
        if (Platform.PersistentMediaRefresh.IsPending(journal))
            return new LaunchOutcome(LaunchStatus.Blocked, "A persistent media refresh retains ownership. Retry its exact refresh-media review before opening Medal.", journal.SessionId);
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
            store.WriteRequest(new HandoffRequest(HandoffRequest.CurrentFormat, Me, NewId(), clock.Now));
            return new LaunchOutcome(LaunchStatus.Waiting,
                $"Medal is {Ownership.Describe(journal.State)} on {where}. A switch request was sent; quit Medal there and this PC can start.");
        }
        return StartSession();
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
            : new LaunchOutcome(finished.Status == FinishStatus.Pending ? LaunchStatus.Waiting : LaunchStatus.Blocked,
                "This PC's previous session must finish first. " + finished.Message);
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
        string session = NewId();
        OwnershipRecord journal;
        try
        {
            journal = store.Update(r => Ownership.Acquire(r, Me, session, clock.Now), options.LockTimeout);
        }
        catch (OwnershipConflictException ex)
        {
            return new LaunchOutcome(LaunchStatus.Waiting, ex.Message);
        }
        RestoreResult restored;
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
            restored = library.Restore(generation.Snapshot, allowEmptyReplacement: true);
            RequireCaptureFolder();
            replication?.Prepare(generation.Snapshot);
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
            if (shutdown?.SuppressUi == true) throw new UnsafeOperationException("Windows is shutting down; Medal was not started.");
        }
        catch (Exception ex)
        {
            // Medal never ran, but ownership can only be given back after the
            // writable mount is verified stopped.
            string cleanup = GiveBackStart(session, ex.Message);
            if (ex is OperationCanceledException && shutdown?.Ending == true)
                return new LaunchOutcome(LaunchStatus.Waiting, "Windows shutdown interrupted storage preparation. This PC can retry at next sign-in." + cleanup);
            if (!IsExpected(ex)) throw;
            return new LaunchOutcome(LaunchStatus.Blocked, ex.Message + cleanup);
        }
        store.Update(r => Ownership.MarkActive(r, Me, session, clock.Now), options.LockTimeout);
        store.RemoveRequest(Me);
        if (shutdown is not null && !shutdown.TryLaunch(medal.Launch))
        {
            var finished = Finish(session);
            return new LaunchOutcome(LaunchStatus.Waiting, finished.Message, session);
        }
        if (shutdown is null) medal.Launch();
        string backup = restored.Changed ? $" This PC's previous library was backed up to {restored.BackupPath}." : "";
        return new LaunchOutcome(LaunchStatus.Started, "Medal started with the shared library." + backup, session);
    }

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
        long current = target.Manifest.Generation;
        for (long? parent = target.Manifest.ParentGeneration; parent is { } generation; )
        {
            if (generation >= current) throw new InvalidDataException($"Generation {current} names a later parent, {generation}.");
            var manifest = store.Generations.ReadManifest(generation);
            if (manifest.SnapshotDigest == digest) return;
            (current, parent) = (generation, manifest.ParentGeneration);
        }
        throw new UnsafeOperationException(
            "This PC's Medal library has changes that were never shared, usually because Medal was opened without medal-sync. "
            + "Nothing was changed. Keep a copy with 'medal-sync snapshot export --out FILE', then run "
            + "'medal-sync launch --replace-local-library' to replace it with the shared library (a backup is kept).");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageBusyException)
        {
            return new FinishOutcome(FinishStatus.Pending,
                $"Clip storage is unreachable or busy ({ex.Message}); the session stays on this PC and will be retried.", -1);
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
        replication?.Publish(snapshot);
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
            return new FinishOutcome(FinishStatus.Unchanged, "Ready to switch: the library did not change." + Forget(session), journal.CommittedGeneration);
        }
        return Publish(journal, session, snapshot, onServer, publishVersions);
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
            generation = later.Count > 0 ? later.Max() + 1 : committed + 1;
            store.Generations.Publish(generation, committed, session, Me, snapshot, AssetManifest.From(onServer), versions, clock.Now);
        }
        shutdown?.RequireTime();
        store.Update(r =>
        {
            shutdown?.RequireTime();
            return Ownership.CompletePublish(r, Me, session, generation, clock.Now);
        }, options.LockTimeout);
        return new FinishOutcome(FinishStatus.Published, $"Ready to switch: generation {generation} published." + Forget(session), generation);
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

    /// <summary>Removes the finished session's cache and login; a failure only leaves files behind.</summary>
    private string Forget(string session)
    {
        try
        {
            mount.Forget(session);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $" The finished session's SSD cache could not be removed yet ({ex.Message}).";
        }
    }

    /// <summary>Null when drained; otherwise the reason to stop for now.</summary>
    private FinishOutcome? WaitForUploads(string session)
    {
        shutdown?.RequireTime();
        if (!mount.IsRunning)
        {
            // After a restart the session's cache may still hold unsent files;
            // the same cache folder resumes them.
            mount.Start(session, forRecording: false);
        }
        var deadline = clock.Now + options.DrainTimeout;
        while (true)
        {
            shutdown?.RequireTime();
            if (Reopened(session) is { } reopened) return reopened;
            var verdict = mount.Drain();
            Report(verdict.State switch
            {
                DrainState.Unknown => SessionPhase.Unknown,
                DrainState.Failed => SessionPhase.RecoveryNeeded,
                _ => SessionPhase.Uploading,
            }, verdict.State == DrainState.Unknown
                ? "Upload status is unknown. Keep this PC on; ownership and cache are retained."
                : "Finishing uploads to primary storage. Keep this PC on and wait for verification.", session,
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
            if (clock.Now >= deadline)
            {
                return new FinishOutcome(FinishStatus.Pending, "Still uploading: " + string.Join(" ", verdict.Reasons), 0);
            }
            Pause(options.PollInterval);
        }
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
        var nextCompatibilityCheck = clock.Now;
        void Once(string message)
        {
            if (told.Add(message)) notify(message);
        }
        while (!cancellation.IsCancellationRequested)
        {
            if (shutdown?.Ending == true) return FinishShutdown(session);
            if (!medal.IsRunning())
            {
                var outcome = Finish(session);
                if (outcome.Status != FinishStatus.Pending) return outcome;
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
                    foreach (var request in store.PendingRequests())
                    {
                        Once($"{request.Device.DisplayName} wants to use Medal. {QuitFromTray} to switch.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Once("Clip storage is unreachable. Keep recording; uploads resume when it returns, and this PC keeps the library until then.");
                }
                var health = mount.Health();
                if (health.Healthy)
                {
                    consecutiveStatusFailures = 0;
                    Report(SessionPhase.Active, "Medal is active. Quit Medal normally from its tray icon when you want to switch PCs.", session);
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

    private void Report(SessionPhase phase, string action, string? session = null, int? files = null, long? bytes = null, string? replica = null, bool isOutcome = false)
    {
        // A disconnected or failed view must never change ownership, uploads or recovery.
        try { progress?.Invoke(new(phase, action, session, files, bytes, replica, isOutcome)); }
        catch (Exception) { /* Telemetry is advisory. */ }
    }

    internal static string NewId() => Guid.NewGuid().ToString("N");
}

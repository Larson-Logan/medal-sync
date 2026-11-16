using System.Data.Common;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Session;

/// <summary>Finish: drain uploads, audit and verify the library, publish the next generation and release ownership.</summary>
internal sealed partial class SessionCoordinator
{
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
        long generation = PrepareGeneration(journal, session, snapshot, onServer, versions);
        shutdown?.RequireTime();
        store.Update(r =>
        {
            shutdown?.RequireTime();
            return Ownership.CompletePublish(r, Me, session, generation, clock.Now);
        }, options.LockTimeout);
        return new FinishOutcome(FinishStatus.Published, $"Ready to switch: generation {generation} published." + Forget(session, generation), generation);
    }

    private long PrepareGeneration(OwnershipRecord journal, string session, LibrarySnapshot snapshot, AssetReport onServer, MedalVersions versions)
    {
        long committed = journal.CommittedGeneration;
        string digest = snapshot.Digest();
        // Folders above the committed generation are either this session's own
        // publish from before a crash (reused) or left by an abandoned session
        // (skipped: never committed, never read).
        var later = store.Generations.Numbered().Where(g => g > committed).OrderDescending().ToList();
        var manifest = AssetManifest.From(onServer);
        long generation = later.FirstOrDefault(g => IsThisAttempt(g, committed, session, digest, versions, manifest), -1L);
        if (generation < 0)
        {
            // Also above the retention high-water mark, so a pruned attempt's number is never reused.
            generation = store.Generations.Next(committed);
            store.Generations.Publish(generation, committed, session, Me, snapshot, manifest, versions, clock.Now);
        }
        return generation;
    }

    private bool IsThisAttempt(long generation, long parent, string session, string digest, MedalVersions versions, AssetManifest assets)
    {
        try
        {
            // Most Active checkpoints are superseded. Reject their small manifests before reading library payloads,
            // and try the newest first so an idle-complete shutdown does not load an entire session's checkpoints.
            var manifest = store.Generations.ReadManifest(generation);
            if (manifest.SessionId != session || manifest.ParentGeneration != parent || manifest.SnapshotDigest != digest || manifest.Medal != versions)
                return false;
            var content = store.Generations.Read(generation);
            return Json.Serialize(content.Assets).AsSpan().SequenceEqual(Json.Serialize(assets));
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
    private List<string> LostEntries(LibrarySnapshot start, LibrarySnapshot now, IAssetChecker? checker = null)
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
            if (paths.Any(p => p is not null && (checker ?? assets).ExistsOnServer(p))) lost.Add(change.Before ?? change.ContentId);
        }
        return lost;
    }
}

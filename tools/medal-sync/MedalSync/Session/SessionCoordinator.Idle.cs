using MedalSync.Coordination;
using MedalSync.Storage;

namespace MedalSync.Session;

internal sealed partial class SessionCoordinator
{
    private DateTimeOffset _nextIdleSync;
    private string _idleStatus = "Clips sync in the background when this PC is idle.";
    private bool _idleSynced;
    private string? _idleDigest;

    private bool IdleAllowed()
    {
        try { return isIdle?.Invoke() == true && !medal.IsRecording(); }
        catch (Exception) { return false; } // An unreadable activity probe never permits background work.
    }

    /// <summary>
    /// Runs in the background supervisor, serialized with finish. Transfer guards run every 200 ms;
    /// quitting or shutdown interrupts them before finish starts, so no two writers overlap.
    /// </summary>
    private void SyncWhileIdle(string session, bool recording, CancellationToken cancellation)
    {
        if (idleSync is null) return;
        if (recording || !IdleAllowed())
        {
            _idleSynced = false;
            _idleStatus = "Background sync waits while this PC is busy.";
            return;
        }
        if (clock.Now < _nextIdleSync)
        {
            // A new clip or metadata edit invalidates a previous all-uploaded indication at the next poll.
            if (_idleSynced)
            {
                try { _idleSynced = mount.Drain().State == DrainState.Drained && library.Read().Digest() == _idleDigest; }
                catch (Exception ex) when (IsExpected(ex)) { _idleSynced = false; }
                if (!_idleSynced) _idleStatus = "New changes are waiting for background sync.";
            }
            return;
        }
        _idleSynced = false;
        _nextIdleSync = clock.Now + TimeSpan.FromSeconds(30);
        var nextOwnershipCheck = clock.Now;
        OwnershipRecord? journal = null;
        void RequireIdle()
        {
            if (cancellation.IsCancellationRequested || shutdown?.Ending == true || !medal.IsRunning()
                || !IdleAllowed()) throw new OperationCanceledException();
            if (clock.Now < nextOwnershipCheck) return;
            var current = store.ReadJournal();
            if (current.State != OwnershipState.Active || !current.IsOwnedBy(Me, session)
                || journal is not null && current != journal) throw new OperationCanceledException();
            journal = current;
            nextOwnershipCheck = clock.Now + TimeSpan.FromSeconds(5);
        }
        try
        {
            RequireIdle();
            var versions = compatibility?.RequirePublish() ?? options.Medal;
            var snapshot = library.Read();
            // The mount's write-back must finish before the persistent copy is used as source.
            var drain = mount.Drain();
            if (drain.State != DrainState.Drained)
            {
                _idleStatus = "Clips are uploading or still being saved. Keep this PC on.";
                return;
            }
            // The user is away by definition here; without this the idle timer could sleep the PC mid-transfer.
            using var awake = StayAwake();
            Report(SessionPhase.Active, "Syncing finished clips in the background.", session);
            if (!idleSync!(snapshot, RequireIdle))
            {
                _idleStatus = "Finished clips sync in the background; waiting for files to settle.";
                return;
            }
            RequireIdle();
            var checker = idleAssets?.Invoke(RequireIdle) ?? assets;
            checker.RequireServer();
            var start = store.Generations.Read(journal!.CommittedGeneration);
            // Never silently accept a deletion/path clearing, even in an uncommitted checkpoint.
            if (LostEntries(start.Snapshot, snapshot, checker).Count > 0)
            {
                _idleStatus = "Clips uploaded; library changes need review when Medal quits.";
                return;
            }
            var onServer = checker.OnServer(snapshot);
            if (onServer.Blocking.Any()) throw new IOException("Primary storage has not verified all clips yet.");
            RequireIdle();
            if (library.Read().Digest() != snapshot.Digest() || store.ReadJournal() != journal
                || (compatibility?.RequirePublish() ?? options.Medal) != versions) throw new OperationCanceledException();
            if (start.Manifest.SnapshotDigest != snapshot.Digest())
            {
                store.RefreshGenerationHistory();
                RequireIdle();
                if (store.ReadJournal() != journal || library.Read().Digest() != snapshot.Digest())
                    throw new OperationCanceledException();
                // Immutable and uncommitted: older clients ignore it; Finish reuses an exact matching attempt.
                // A concurrent abandon can leave an orphan, but can never commit or release this session here.
                PrepareGeneration(journal, session, snapshot, onServer, versions);
            }
            RequireIdle();
            _idleDigest = snapshot.Digest();
            _idleSynced = true;
            _idleStatus = "All clips uploaded. Library checkpoint prepared; quit Medal normally to finish the handoff.";
        }
        catch (OperationCanceledException)
        {
            _idleStatus = "Background sync paused; this PC keeps the library and cached clips.";
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _idleStatus = "Background sync will retry: " + ex.Message + " This PC keeps the library and clips.";
            _nextIdleSync = clock.Now + TimeSpan.FromMinutes(1);
        }
    }
}

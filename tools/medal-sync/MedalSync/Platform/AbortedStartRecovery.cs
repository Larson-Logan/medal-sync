using MedalSync.Config;
using MedalSync.Coordination;

namespace MedalSync.Platform;

/// <summary>
/// Conservative recovery for older Clean journals whose empty-cache startup login was orphaned, and for a published
/// session whose cache or login could not be removed when it finished.
/// </summary>
internal sealed class AbortedStartRecovery(MedalSyncConfig config, LocalState state, CoordinationStore store, Action requireIdle,
    Action<string>? deleteDirectory = null)
{
    public string Run(string session, bool apply)
    {
        if (!CoordinationStore.IsValidId(session)) throw new InvalidDataException("A session ID is 32 lowercase hex characters.");
        if (state.FinishedSessionIds().Contains(session)) return Finished(session, apply);
        string cache = config.SessionCacheDirectory(session);
        string login = Path.Combine(state.Root, "sessions", session + ".json");
        void RequireSafe()
        {
            requireIdle();
            var journal = store.ReadJournal();
            if (journal.State != OwnershipState.Clean || journal.Owner is not null || journal.SessionId is not null)
                throw new UnsafeOperationException("The library is not Ready. Recover its owned session with finish before recovering an orphaned startup login.");
            if (!File.Exists(login)) throw new UnsafeOperationException("No unfinished login matches that session. Check the exact retained session ID.");
            if (File.Exists(cache) || Directory.Exists(cache) && (File.GetAttributes(cache).HasFlag(FileAttributes.ReparsePoint)
                || Directory.EnumerateFileSystemEntries(cache).Any()))
                throw new UnsafeOperationException("This older session has nonempty or uncertain cache state. Its login and cache are retained; do not bypass upload recovery or setup readiness.");
        }
        RequireSafe();
        if (!apply) return "Preview only: the stopped session has no cached files. Its login will be retained under aborted-starts; add --apply for this exact session.";
        using var writer = store.AcquireWriterLock(TimeSpan.FromSeconds(10));
        RequireSafe();
        string recovery = state.RetainAbortedStart(session, config.LibraryId, cache, DateTimeOffset.UtcNow, legacyEmptyCache: true);
        return $"Orphaned startup login retained at {recovery}. No cache, library or journal was deleted or published. Retry setup readiness.";
    }

    /// <summary>Its receipt records that its uploads were verified and its generation committed, so its leftovers are safe to remove.</summary>
    private string Finished(string session, bool apply)
    {
        requireIdle();
        var receipt = state.ReadFinished(session);
        if (receipt.LibraryId != config.LibraryId)
            throw new UnsafeOperationException("That finished session belongs to another library's configuration; it was kept.");
        if (!apply)
            return $"Preview only: session {session} finished and generation {receipt.Generation} was committed, but its SSD cache or login was not removed. "
                + "Add --apply to remove them. This does not block setup.";
        var left = FinishedSessionCleanup.Retry(state, config.LibraryId, config.SessionCacheDirectory, deleteDirectory: deleteDirectory);
        if (left.FirstOrDefault(l => l.Contains(session, StringComparison.Ordinal)) is { } problem)
            throw new UnsafeOperationException($"{problem} Close any program using the cache folder and retry.");
        return $"Removed the leftover SSD cache and login of finished session {session} (generation {receipt.Generation})."
            + (left.Count == 0 ? "" : $" {left.Count} other finished session(s) are still left: {left[0]}");
    }
}

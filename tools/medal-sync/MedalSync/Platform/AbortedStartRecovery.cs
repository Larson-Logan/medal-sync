using MedalSync.Config;
using MedalSync.Coordination;

namespace MedalSync.Platform;

/// <summary>Conservative recovery for older Clean journals whose empty-cache startup login was orphaned.</summary>
internal sealed class AbortedStartRecovery(MedalSyncConfig config, LocalState state, CoordinationStore store, Action requireIdle)
{
    public string Run(string session, bool apply)
    {
        if (!CoordinationStore.IsValidId(session)) throw new InvalidDataException("A session ID is 32 lowercase hex characters.");
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
}

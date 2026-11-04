using MedalSync.Config;

namespace MedalSync.Platform;

/// <summary>
/// Removes a published session's SSD cache and control login. The login first moves out of <c>sessions</c> beside a
/// receipt naming the committed generation, so if a delete fails (antivirus, the indexer or a backup client holding a
/// cached clip), what is left is recognizably safe, no longer blocks setup, and is retried later.
/// </summary>
internal static class FinishedSessionCleanup
{
    /// <summary>Only after uploads drained and verified, the generation is committed and the drive stopped.</summary>
    public static void Forget(LocalState state, string libraryId, string session, string cache, long generation, DateTimeOffset now,
        Action<string>? deleteDirectory = null)
    {
        state.RetireSession(session, libraryId, cache, generation, now);
        Remove(state, session, cache, deleteDirectory);
    }

    /// <summary>
    /// Retries this library's receipted sessions only; a login without a receipt is never touched. Returns what is still
    /// left, one line each. Never throws for a file that cannot be removed yet.
    /// </summary>
    /// <param name="cacheFor">The cache folder the configuration names for a session; a receipt naming any other folder is kept.</param>
    public static IReadOnlyList<string> Retry(LocalState state, string libraryId, Func<string, string> cacheFor, string? except = null,
        Action<string>? deleteDirectory = null)
    {
        IReadOnlyList<string> sessions;
        try { sessions = state.FinishedSessionIds(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return [$"Finished sessions could not be listed ({ex.Message})."]; }
        var left = new List<string>();
        foreach (string session in sessions)
        {
            if (session == except) continue;
            try
            {
                if (state.IsEmptyFinished(session))
                {
                    state.ForgetFinished(session); // Its cache and evidence are already gone; only the folder was left.
                    continue;
                }
                var receipt = state.ReadFinished(session);
                if (receipt.LibraryId != libraryId) continue; // Another library's; its configuration names its cache.
                string cache = cacheFor(session);
                if (receipt.Cache != cache)
                    throw new InvalidDataException($"its receipt names {receipt.Cache}, not {cache}, so it was kept");
                Forget(state, libraryId, session, cache, receipt.Generation, receipt.FinishedAt, deleteDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                left.Add($"Finished session {session}: {ex.Message}");
            }
        }
        return left;
    }

    private static void Remove(LocalState state, string session, string cache, Action<string>? deleteDirectory)
    {
        if (Directory.Exists(cache)) (deleteDirectory ?? (path => Directory.Delete(path, recursive: true)))(cache);
        state.ForgetFinished(session);
    }
}

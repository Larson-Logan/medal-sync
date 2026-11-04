using MedalSync.Util;

namespace MedalSync.Coordination;

/// <summary>
/// Marks the ownership <c>enroll --apply</c> holds. A killed apply leaves it STARTING (or RECOVERY_REQUIRED after a caught
/// failure), which otherwise reads as a launch that never started Medal. Launch and finish must never give it back: this
/// PC's library or capture folder may already be half-way to the union, and only <c>enroll recover</c> knows which.
/// </summary>
internal static class EnrollmentOwnership
{
    internal const string ReasonPrefix = "enrollment:";
    private const string PendingFile = "enrollment-pending.json";

    public static string Reason(string session) => ReasonPrefix + session;

    public static bool IsPending(OwnershipRecord journal) => journal.Reason?.StartsWith(ReasonPrefix, StringComparison.Ordinal) == true;

    /// <summary>What launch and finish say instead of releasing this PC's interrupted enrollment.</summary>
    public static string Interrupted(string? reviewFolder)
    {
        string folder = reviewFolder ?? "REVIEW_FOLDER";
        string where = reviewFolder is null ? "the folder you passed to 'enroll --out'" : reviewFolder;
        return $"This PC's enrollment was interrupted while it held the library, so the library was not given back and nothing was changed. "
            + $"apply.json in {where} records where it stopped. With Medal quit, preview 'medal-sync enroll recover --out \"{folder}\"' to finish "
            + "enrolling, or add --rollback to return to this PC's original library and capture folder; add --apply to act. "
            + "Do not use 'launch --replace-local-library': it would drop this PC's own clips from Medal.";
    }

    internal sealed record Pending(int Format, string Session, string Directory);

    /// <summary>Written before ownership is taken, so launch and finish on this PC can name the review folder.</summary>
    public static void WritePending(string stateRoot, string session, string directory)
    {
        System.IO.Directory.CreateDirectory(stateRoot);
        AtomicFile.Replace(Path.Combine(stateRoot, PendingFile), Json.Serialize(new Pending(1, session, directory)));
    }

    /// <summary>The review folder of the pending enrollment, or null when none is recorded or it cannot be read.</summary>
    public static string? PendingFolder(string stateRoot, string? session = null)
    {
        try
        {
            string path = Path.Combine(stateRoot, PendingFile);
            if (!File.Exists(path)) return null;
            var pending = Json.ReadFile<Pending>(path, "Pending enrollment");
            return pending.Format == 1 && (session is null || pending.Session == session) ? pending.Directory : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    public static void ClearPending(string stateRoot)
    {
        string path = Path.Combine(stateRoot, PendingFile);
        if (File.Exists(path)) File.Delete(path);
    }
}

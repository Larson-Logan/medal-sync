using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>
/// Sorts an rclone failure into one that waiting may fix (an outage) and one that needs the user
/// (sign-in, configuration, a conflicting file, quota, failed verification), and words the second
/// kind with a next step. Exit codes follow rclone's documentation: 1 uncategorised, 2 usage,
/// 3 directory not found, 4 file not found, 5 temporary, 6 no-retry, 7 fatal.
/// </summary>
internal static class RcloneFailure
{
    private const RcloneHints NeedsUser = RcloneHints.SignIn | RcloneHints.Config | RcloneHints.Quota | RcloneHints.Conflict;

    /// <summary>
    /// True for a network symptom, a timeout, the temporary-error exit 5, or an uncategorised exit 1 with no other clue.
    /// rclone also exits 1 when it cannot reach a provider while opening the remote, so that case stays a wait.
    /// A failed <c>check</c> is a real difference, never an outage.
    /// </summary>
    public static bool IsTransient(RcloneFailedException ex)
    {
        if (ex.Hints.HasFlag(RcloneHints.Network) || ex.TimedOut) return true;
        if ((ex.Hints & NeedsUser) != 0) return false;
        return ex.ExitCode switch
        {
            5 => true,
            1 => ex.Command != "check",
            _ => false,
        };
    }

    /// <param name="where">What failed, for example "Primary storage onedrive:MedalSync".</param>
    /// <param name="destination">The rclone path that failed; names the remote to reconnect.</param>
    /// <param name="credentials">The configured rclone credential file, when there is one.</param>
    public static string Describe(RcloneFailedException ex, string where, string destination, string? credentials)
    {
        // Could not start rclone, the credential file is missing, or the output was refused: the message is already specific.
        if (ex.ExitCode is null && !ex.TimedOut) return $"{where}: {ex.Message}";
        string config = credentials is null ? "" : $" --config \"{credentials}\"";
        if (ex.Hints.HasFlag(RcloneHints.SignIn))
            return Remote(destination) is { } remote
                ? $"{where} did not accept rclone's sign-in; it may have expired or been revoked. Run 'rclone config reconnect \"{remote}:\"{config}' on this PC to sign in again."
                : $"{where} did not accept rclone's sign-in. Sign in to the provider again with 'rclone config'{config}.";
        if (ex.Hints.HasFlag(RcloneHints.Config))
            return $"rclone could not use its configuration for {where}. Check that {credentials ?? "the rclone config file"} defines "
                + $"{(Remote(destination) is { } name ? $"the remote '{name}'" : "the remote")} and opens without a password prompt.";
        if (ex.Hints.HasFlag(RcloneHints.Quota))
            return $"{where} is out of space or over its quota. Free space there or raise the quota.";
        if (ex.Hints.HasFlag(RcloneHints.Conflict))
            return $"{where} already holds a different file at one of this library's clip paths, and the STOP conflict policy keeps existing files. "
                + "Move the conflicting file aside, or set replication.conflictPolicy to \"VERSION\" to keep both.";
        if (ex.Command == "check" && ex.ExitCode == 1)
            return $"Verification found files at {where} that are missing or differ from this PC's copy. Check whether another program changed or removed files there.";
        if (ex.ExitCode == 2)
            return $"rclone refused its command line for {where} (exit 2). Check that rclonePath points to the rclone version this Medal Sync release installs.";
        return $"rclone {ex.Command ?? "transfer"} for {where} failed (exit {ex.ExitCode}). Check provider access, quota and conflicting files there.";
    }

    /// <summary>The rclone remote name in <c>remote:path</c>, or null for a local or UNC folder.</summary>
    internal static string? Remote(string destination)
    {
        if (WindowsPath.TryParse(destination, out _, out _)) return null;
        int colon = destination.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? destination[..colon] : null;
    }
}

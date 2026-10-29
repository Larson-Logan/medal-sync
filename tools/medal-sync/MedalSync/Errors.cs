namespace MedalSync;

/// <summary>A refusal the user should read. The message says what to do next.</summary>
internal class MedalSyncException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The library, schema, account or Medal version is not one this build supports.</summary>
internal sealed class IncompatibleLibraryException(string message, Exception? inner = null)
    : MedalSyncException(message, inner);

/// <summary>The ownership journal does not permit the requested step.</summary>
internal sealed class OwnershipConflictException(string message) : MedalSyncException(message);

/// <summary>A precondition for a safe mutation is not met (Medal running, mount missing, ...).</summary>
internal class UnsafeOperationException(string message) : MedalSyncException(message);

/// <summary>Another PC holds the journal's writer lock for a moment; retrying shortly is safe.</summary>
internal sealed class StorageBusyException(string message) : UnsafeOperationException(message);

/// <summary>Primary storage or its journal cannot be reached right now. Media and ownership stay on this PC; retrying later is safe.</summary>
internal sealed class StorageOfflineException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Primary storage or a required replica refused the work in a way that waiting alone will not fix: sign-in, configuration,
/// a conflicting file, a full quota or failed verification. Ownership and media stay on this PC. The message names the next step.
/// </summary>
internal sealed class StorageActionRequiredException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>What rclone's error output said, kept as categories only: the text itself can contain credentials or private filenames.</summary>
[Flags]
internal enum RcloneHints
{
    None = 0,
    Network = 1,
    SignIn = 2,
    Config = 4,
    Quota = 8,
    Conflict = 16,
}

/// <summary>An rclone transfer, check or listing did not succeed. Never proof of success.</summary>
internal sealed class RcloneFailedException(string message, int? exitCode = null, Exception? inner = null,
    RcloneHints hints = RcloneHints.None, bool timedOut = false, string? command = null) : IOException(message, inner)
{
    public int? ExitCode { get; } = exitCode;

    public RcloneHints Hints { get; } = hints;

    public bool TimedOut { get; } = timedOut;

    /// <summary>The rclone command that failed (copy, check, lsjson, ...), when known.</summary>
    public string? Command { get; } = command;
}

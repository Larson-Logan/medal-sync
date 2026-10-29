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

/// <summary>An rclone transfer, check or listing did not succeed. Never proof of success.</summary>
internal sealed class RcloneFailedException(string message, int? exitCode = null, Exception? inner = null) : IOException(message, inner)
{
    public int? ExitCode { get; } = exitCode;
}

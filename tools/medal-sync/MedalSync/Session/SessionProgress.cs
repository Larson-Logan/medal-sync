namespace MedalSync.Session;

internal enum SessionPhase { Starting, Active, Uploading, Verifying, Ready, Waiting, RecoveryNeeded, Unknown }

/// <summary>Advisory UI telemetry. It never authorizes a journal transition.</summary>
internal sealed record SessionProgress(SessionPhase Phase, string NextAction, string? SessionId = null,
    int? QueuedFiles = null, long? QueuedBytes = null, string? Replica = null);

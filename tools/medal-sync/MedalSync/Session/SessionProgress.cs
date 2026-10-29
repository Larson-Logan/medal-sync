namespace MedalSync.Session;

/// <summary>Appended values keep stored status readable. WaitingForStorage is an outage with media and ownership safe on this PC.</summary>
internal enum SessionPhase { Starting, Active, Uploading, Verifying, Ready, Waiting, RecoveryNeeded, Unknown, WaitingForStorage }

/// <summary>Advisory UI telemetry. It never authorizes a journal transition.</summary>
internal sealed record SessionProgress(SessionPhase Phase, string NextAction, string? SessionId = null,
    int? QueuedFiles = null, long? QueuedBytes = null, string? Replica = null, bool IsOutcome = false);

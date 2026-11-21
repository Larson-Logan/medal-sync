using System.Text.Json.Serialization;

namespace MedalSync.Coordination;

internal enum OwnershipState
{
    /// <summary>The last generation is committed; any PC may start a session.</summary>
    Clean,

    /// <summary>The owner is restoring its library and mounting storage. Medal is not running yet.</summary>
    Starting,

    /// <summary>Medal and the owner's mount may write.</summary>
    Active,

    /// <summary>Medal has exited; the owner is finishing uploads.</summary>
    Draining,

    /// <summary>The owner is committing the next generation.</summary>
    Publishing,

    /// <summary>Interrupted or uncertain. Only the owner, or an explicit abandon, may continue.</summary>
    RecoveryRequired,

    Maintenance,

    Migrating,

    Migrated,
}

internal sealed record DeviceIdentity(string DeviceId, string DisplayName);

/// <summary>The persistent ownership journal kept in the coordination directory.</summary>
internal sealed record OwnershipRecord(
    int Format,
    string LibraryId,
    string AccountId,
    OwnershipState State,
    DeviceIdentity? Owner,
    string? SessionId,
    long CommittedGeneration,
    long Revision,
    DateTimeOffset UpdatedAt,
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? MaintenanceExpiresAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OperationDigest = null)
{
    public const int CurrentFormat = 1;

    public bool IsOwnedBy(DeviceIdentity device, string session) =>
        Owner?.DeviceId == device.DeviceId && SessionId == session;
}

/// <summary>
/// Every allowed change to the ownership journal. A dirty session is never
/// handed to another PC because a lock vanished or time passed: only the owner
/// can move it forward, and the sole escape hatch is an explicit
/// <see cref="Abandon"/> that names the session being given up.
/// </summary>
internal static class Ownership
{
    public static OwnershipRecord Initial(string libraryId, string accountId, DateTimeOffset now) =>
        new(OwnershipRecord.CurrentFormat, libraryId, accountId, OwnershipState.Clean, null, null, 0, 1, now, "generation 0 published");

    public static OwnershipRecord Acquire(OwnershipRecord current, DeviceIdentity device, string session, DateTimeOffset now)
    {
        if (current.State != OwnershipState.Clean) throw Busy(current, device);
        return Next(current, OwnershipState.Starting, device, session, now, null);
    }

    public static OwnershipRecord BeginMaintenance(OwnershipRecord current, DeviceIdentity device, string operation,
        string command, string digest, DateTimeOffset now)
    {
        if (current.State != OwnershipState.Clean) throw Busy(current, device);
        return Next(current, OwnershipState.Maintenance, device, operation, now, command) with
        {
            MaintenanceExpiresAt = now.AddHours(24),
            OperationDigest = digest,
        };
    }

    public static OwnershipRecord EndMaintenance(OwnershipRecord current, DeviceIdentity device, string operation, DateTimeOffset now, bool completed = true)
    {
        Require(current, device, operation, OwnershipState.Maintenance);
        return Next(current, OwnershipState.Clean, null, null, now,
            completed ? MaintenanceCompletion(current.OperationDigest!) : "maintenance review refused");
    }

    internal static string MaintenanceCompletion(string digest) => "maintenance completed: " + digest;

    public static OwnershipRecord BeginMigration(OwnershipRecord current, DeviceIdentity device, string operation, string digest, DateTimeOffset now)
    {
        if (current.State != OwnershipState.Clean) throw Busy(current, device);
        return Next(current, OwnershipState.Migrating, device, operation, now, "moving to Google Drive cloud mode") with { OperationDigest = digest };
    }

    public const string MigratedMessage = "This library moved to Google Drive cloud mode. Update this PC's configuration.";

    public static OwnershipRecord EndMigration(OwnershipRecord current, DeviceIdentity device, string operation, bool rollback, DateTimeOffset now)
    {
        Require(current, device, operation, OwnershipState.Migrating);
        return rollback ? Next(current, OwnershipState.Clean, null, null, now, "migration rolled back")
            : Next(current, OwnershipState.Migrated, device, operation, now, MigratedMessage) with { OperationDigest = current.OperationDigest };
    }

    public static OwnershipRecord ActivateMigration(OwnershipRecord current, DeviceIdentity device, string operation, DateTimeOffset now)
    {
        Require(current, device, operation, OwnershipState.Migrating);
        return Next(current, OwnershipState.Clean, null, null, now, "cloud migration complete");
    }

    public static OwnershipRecord MarkActive(OwnershipRecord current, DeviceIdentity device, string session, DateTimeOffset now)
    {
        Require(current, device, session, OwnershipState.Starting);
        return Next(current, OwnershipState.Active, device, session, now, null);
    }

    /// <summary>Publishes a verified snapshot while the current session keeps ownership.</summary>
    public static OwnershipRecord CommitCheckpoint(OwnershipRecord current, DeviceIdentity device, string session, long generation, DateTimeOffset now)
    {
        Require(current, device, session, OwnershipState.Active);
        if (generation <= current.CommittedGeneration)
            throw new OwnershipConflictException($"Checkpoint {generation} must be newer than generation {current.CommittedGeneration}.");
        return Next(current, OwnershipState.Active, device, session, now, null) with { CommittedGeneration = generation };
    }

    /// <summary>Gives a session back before Medal ever started. Nothing can have been written.</summary>
    public static OwnershipRecord AbortStart(OwnershipRecord current, DeviceIdentity device, string session, string reason, DateTimeOffset now)
    {
        Require(current, device, session, OwnershipState.Starting);
        return Next(current, OwnershipState.Clean, null, null, now, $"start aborted: {reason}");
    }

    public static OwnershipRecord BeginDrain(OwnershipRecord current, DeviceIdentity device, string session, DateTimeOffset now)
    {
        if (current.State == OwnershipState.Draining && current.IsOwnedBy(device, session)) return current;
        Require(current, device, session, OwnershipState.Active, OwnershipState.RecoveryRequired);
        return Next(current, OwnershipState.Draining, device, session, now, null);
    }

    public static OwnershipRecord BeginPublish(OwnershipRecord current, DeviceIdentity device, string session, DateTimeOffset now)
    {
        if (current.State == OwnershipState.Publishing && current.IsOwnedBy(device, session)) return current;
        Require(current, device, session, OwnershipState.Draining);
        return Next(current, OwnershipState.Publishing, device, session, now, null);
    }

    public static OwnershipRecord CompletePublish(OwnershipRecord current, DeviceIdentity device, string session, long generation, DateTimeOffset now)
    {
        Require(current, device, session, OwnershipState.Publishing);
        // Numbers can skip: a generation written by an abandoned session is left
        // in place, never committed, and the next publish takes a higher number.
        if (generation <= current.CommittedGeneration)
        {
            throw new OwnershipConflictException(
                $"Generation {generation} is not newer than the committed generation {current.CommittedGeneration}.");
        }
        return Next(current, OwnershipState.Clean, null, null, now, $"generation {generation} published by {device.DisplayName}") with
        {
            CommittedGeneration = generation,
        };
    }

    /// <summary>Ends a session whose library equals the committed generation; no new generation is needed.</summary>
    public static OwnershipRecord CompleteUnchanged(OwnershipRecord current, DeviceIdentity device, string session, DateTimeOffset now)
    {
        Require(current, device, session, OwnershipState.Publishing);
        return Next(current, OwnershipState.Clean, null, null, now, $"session ended by {device.DisplayName} without library changes");
    }

    public static OwnershipRecord RequireRecovery(OwnershipRecord current, DeviceIdentity device, string session, string reason, DateTimeOffset now)
    {
        if (current.State == OwnershipState.RecoveryRequired && current.IsOwnedBy(device, session) && current.Reason == reason) return current;
        Require(current, device, session, OwnershipState.Starting, OwnershipState.Active, OwnershipState.Draining,
            OwnershipState.Publishing, OwnershipState.RecoveryRequired);
        return Next(current, OwnershipState.RecoveryRequired, device, session, now, reason);
    }

    /// <summary>
    /// The owner resumes its own session (same device and session ID): after an
    /// interruption, or because Medal was opened again while it was draining.
    /// </summary>
    public static OwnershipRecord ResumeActive(OwnershipRecord current, DeviceIdentity device, string session, DateTimeOffset now)
    {
        Require(current, device, session, OwnershipState.Draining, OwnershipState.RecoveryRequired);
        return Next(current, OwnershipState.Active, device, session, now, "resumed by its owner");
    }

    /// <summary>
    /// Explicitly gives up another PC's unfinished session, returning to the last
    /// committed generation. Its unpublished work stays on that PC (and possibly
    /// in its cache); the operator must have inventoried it. The caller must type
    /// the exact session ID being abandoned.
    /// </summary>
    public static OwnershipRecord Abandon(OwnershipRecord current, DeviceIdentity operatorDevice, string confirmedSession, string reason, DateTimeOffset now)
    {
        if (current.State is OwnershipState.Clean or OwnershipState.Migrating or OwnershipState.Migrated)
            throw new OwnershipConflictException("There is no unfinished session to abandon.");
        if (current.State == OwnershipState.Maintenance && !(current.MaintenanceExpiresAt <= now))
            throw new OwnershipConflictException("Wait until the maintenance review time has passed before clearing it. Retry the original command on its owner PC if possible.");
        if (string.IsNullOrWhiteSpace(reason)) throw new OwnershipConflictException("Abandoning a session needs a recorded reason.");
        if (confirmedSession != current.SessionId)
        {
            throw new OwnershipConflictException($"The confirmation does not match the unfinished session {current.SessionId}. Nothing was changed.");
        }
        return Next(current, OwnershipState.Clean, null, null, now,
            $"session {current.SessionId} of {current.Owner?.DisplayName} ({current.State}) abandoned by {operatorDevice.DisplayName}: {reason}");
    }

    private static OwnershipRecord Next(OwnershipRecord current, OwnershipState state, DeviceIdentity? owner, string? session,
        DateTimeOffset now, string? reason) =>
        current with
        {
            State = state,
            Owner = owner,
            SessionId = session,
            Revision = current.Revision + 1,
            UpdatedAt = now,
            Reason = reason,
            MaintenanceExpiresAt = null,
            OperationDigest = null,
        };

    private static void Require(OwnershipRecord current, DeviceIdentity device, string session, params OwnershipState[] states)
    {
        if (current.Owner?.DeviceId != device.DeviceId) throw Busy(current, device);
        if (current.SessionId != session)
        {
            throw new OwnershipConflictException(
                $"This PC owns session {current.SessionId}, not {session}. Recover that session instead of starting another.");
        }
        if (!states.Contains(current.State))
        {
            throw new OwnershipConflictException($"Session {session} is {current.State}; expected {string.Join(" or ", states)}.");
        }
    }

    private static OwnershipConflictException Busy(OwnershipRecord current, DeviceIdentity device) =>
        current.Owner is null
            ? new OwnershipConflictException($"The library is {current.State}, so {device.DisplayName} cannot take this step.")
            : new OwnershipConflictException(
                $"Medal's library is {Describe(current.State)} on {current.Owner.DisplayName} (since {current.UpdatedAt:yyyy-MM-dd HH:mm} UTC). "
                + (current.State == OwnershipState.RecoveryRequired
                    ? "That PC must recover its session first."
                    : IsFinishing(current.State) ? "Wait for it to finish uploading to switch PCs." : "Quit Medal there to switch PCs."));

    /// <summary>Medal was quit on the owning PC and it is uploading: it releases the library by itself, with nobody needing to quit Medal there.</summary>
    public static bool IsFinishing(OwnershipState state) => state is OwnershipState.Draining or OwnershipState.Publishing;

    public static string HandoffAdvice(OwnershipState? state)
    {
        if (state == OwnershipState.Maintenance) return "Wait for maintenance to finish on that PC.";
        string next = state switch
        {
            OwnershipState.Draining or OwnershipState.Publishing => "Keep that PC on while it finishes the handoff.",
            OwnershipState.Active => "If Medal is open there, quit it from its tray icon.",
            OwnershipState.Starting => "Let startup finish there, then quit Medal to hand off.",
            OwnershipState.RecoveryRequired => "Finish recovery on that PC.",
            _ => "Wait for the owning PC to finish the handoff.",
        };
        return next + " If that PC is off, asleep or signed out, turn it on and sign in. If needed, use Retry finish there with Medal closed.";
    }

    /// <summary>
    /// A finishing owner normally hands off within minutes. A journal unchanged for this long suggests it was switched off,
    /// slept or signed out before finishing; it finishes at its next sign-in. Only wording changes: nothing is handed over.
    /// </summary>
    public static readonly TimeSpan QuietFinish = TimeSpan.FromMinutes(30);

    /// <summary>The journal's last change when another PC's finishing session has been quiet for <see cref="QuietFinish"/>.</summary>
    public static DateTimeOffset? QuietSince(OwnershipRecord journal, DateTimeOffset now) =>
        IsFinishing(journal.State) && journal.Owner is not null && now - journal.UpdatedAt >= QuietFinish ? journal.UpdatedAt : null;

    /// <summary>What a waiting PC says about a quiet finishing owner: where the unuploaded clips are and how the handoff resumes.</summary>
    public static string QuietFinishMessage(string where, DateTimeOffset since) =>
        $"{where} has not handed off the library since {since.UtcDateTime:yyyy-MM-dd HH:mm} UTC. If it is off, asleep or signed out, "
        + "turn it on and sign in. If needed, use Retry finish there with Medal closed. Unuploaded clips are still on that PC. "
        + "If you know it fully shut down, open Medal Library here and choose Use last synced library.";

    public static string Describe(OwnershipState state) => state switch
    {
        OwnershipState.Clean => "ready",
        OwnershipState.Starting => "starting",
        OwnershipState.Active => "in use",
        OwnershipState.Draining => "uploading",
        OwnershipState.Publishing => "switching",
        OwnershipState.RecoveryRequired => "waiting for recovery",
        OwnershipState.Maintenance => "under maintenance",
        OwnershipState.Migrating => "migrating to cloud storage",
        OwnershipState.Migrated => "moved to Google Drive cloud mode",
        _ => state.ToString(),
    };
}

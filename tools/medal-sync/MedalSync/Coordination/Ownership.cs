namespace LarsonServer.MedalSync.Coordination;

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
    string? Reason)
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

    public static OwnershipRecord MarkActive(OwnershipRecord current, DeviceIdentity device, string session, DateTimeOffset now)
    {
        Require(current, device, session, OwnershipState.Starting);
        return Next(current, OwnershipState.Active, device, session, now, null);
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
        if (current.State == OwnershipState.Clean) throw new OwnershipConflictException("There is no unfinished session to abandon.");
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
                    : "Quit Medal there to switch PCs."));

    public static string Describe(OwnershipState state) => state switch
    {
        OwnershipState.Clean => "ready",
        OwnershipState.Starting => "starting",
        OwnershipState.Active => "in use",
        OwnershipState.Draining => "uploading",
        OwnershipState.Publishing => "switching",
        OwnershipState.RecoveryRequired => "waiting for recovery",
        _ => state.ToString(),
    };
}

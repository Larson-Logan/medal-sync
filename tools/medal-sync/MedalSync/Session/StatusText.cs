using MedalSync.Coordination;

namespace MedalSync.Session;

/// <summary>The everyday status words from the plan, from this PC's point of view.</summary>
internal static class StatusText
{
    public static string Label(OwnershipRecord journal, DeviceIdentity me)
    {
        bool mine = journal.Owner?.DeviceId == me.DeviceId;
        return journal.State switch
        {
            OwnershipState.Clean => "Ready",
            OwnershipState.RecoveryRequired => "Recovery needed",
            _ when !mine => "Waiting for the other PC",
            OwnershipState.Starting => "Starting",
            OwnershipState.Active => "Recording/Active",
            OwnershipState.Draining => "Uploading",
            OwnershipState.Publishing => "Switching PC",
            _ => journal.State.ToString(),
        };
    }

    public static string For(OwnershipRecord journal, DeviceIdentity me, bool? clipDrivePresent = null)
    {
        if (clipDrivePresent == false && journal.State == OwnershipState.Active && journal.Owner?.DeviceId == me.DeviceId)
            return $"Recovery needed: this PC still owns generation {journal.CommittedGeneration}, but its clip drive is stopped. "
                + "Quit Medal from its tray, then open Medal Library to recover the unfinished session before switching PCs.";
        string label = Label(journal, me);
        string where = journal.Owner is null ? "" : $" on {journal.Owner.DisplayName}";
        string reason = journal.Reason is null ? "" : $" ({journal.Reason})";
        return $"{label}: library {journal.State}{where}, generation {journal.CommittedGeneration}, updated {journal.UpdatedAt:yyyy-MM-dd HH:mm} UTC{reason}.";
    }
}

using MedalSync.Coordination;

namespace MedalSync.Config;

internal static class DeviceNameAdvice
{
    public static string? For(MedalSyncConfig config, DeviceIdentity me, IEnumerable<DeviceIdentity> published) =>
        config.DeviceLabel is null && published.Any(other => other.DeviceId != me.DeviceId
            && string.Equals(other.DisplayName, me.DisplayName, StringComparison.OrdinalIgnoreCase))
            ? "Another PC has published under the same display name. Set a PC display name in Configure (deviceLabel in config.json) so messages distinguish the PCs."
            : null;

    // Advisory only, requested by status and setup, never used to decide ownership.
    public static string? Read(MedalSyncConfig config, LocalState state, CoordinationStore store)
    {
        if (config.DeviceLabel is not null) return null;
        var me = File.Exists(Path.Combine(state.Root, "device.json")) ? state.ExistingDevice()
            : new DeviceIdentity("", $"{Environment.MachineName} ({Environment.UserName})");
        me = me with { DisplayName = $"{Environment.MachineName} ({Environment.UserName})" };
        return For(config, me, PublishedDevices(store.Generations));
    }

    private static IEnumerable<DeviceIdentity> PublishedDevices(GenerationStore generations)
    {
        foreach (long number in generations.Numbered())
        {
            DeviceIdentity device;
            try { device = generations.ReadManifest(number).Device; }
            catch (InvalidDataException) { continue; } // A damaged historical manifest cannot provide name advice.
            yield return device;
        }
    }
}

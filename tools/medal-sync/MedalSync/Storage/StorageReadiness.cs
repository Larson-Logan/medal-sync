using MedalSync.Config;

namespace MedalSync.Storage;

/// <summary>
/// Whether this PC's folders and disks can hold a configuration, checked before it is saved.
/// Reads only; never creates folders or contacts a provider.
/// </summary>
internal static class StorageReadiness
{
    public static IReadOnlyList<string> Problems(MedalSyncConfig config, Func<string, bool> folderExists, Func<string, VolumeInfo?> describe)
    {
        var problems = new List<string>();
        // An rclone primary is a provider folder, not a path on this PC; its credentials are checked below.
        var folders = new List<string> { config.CoordinationRoot, config.CacheRoot };
        if (!config.PrimaryIsRclone) folders.Insert(0, config.Remote);
        if (config.Replication?.LocalCopyRoot is { } copy) folders.Add(copy);
        foreach (string folder in folders)
            if (!folderExists(folder))
                problems.Add($"Storage folder is unavailable: '{folder}'. Create the dedicated folder or reconnect the share, then review again.");
        if (describe(config.CacheRoot) is { } cache)
        {
            problems.AddRange(StorageVolume.Problems(StorageRole.Cache, config.CacheRoot, cache));
            if (cache.FreeBytes < ((long)config.Mount.MinFreeGiB << 30))
                problems.Add($"The cache drive ({cache.MountPath}) has {StorageVolume.Gb(cache.FreeBytes)} free, below the configured free-space reserve. "
                    + "Free space or select another SSD before saving.");
        }
        if (config.PersistentMediaRoot is { } media && describe(media) is { } volume)
            problems.AddRange(StorageVolume.Problems(StorageRole.Media, media, volume));
        if ((config.Replication?.Targets.Count > 0 || config.PrimaryIsRclone)
            && config.Replication?.RcloneConfigPath is { } credentials && !File.Exists(Environment.ExpandEnvironmentVariables(credentials)))
            problems.Add("The provider configuration file is missing. Authorize providers separately with 'rclone config'; keep credentials outside media and coordination folders.");
        return problems;
    }
}

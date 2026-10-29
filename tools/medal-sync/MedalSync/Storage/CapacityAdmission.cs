using MedalSync.Config;

namespace MedalSync.Storage;

internal sealed record CapacityInputs(long FreeBytes, long ReserveBytes, double BitrateMbps, TimeSpan MaxSession, long PendingUploadBytes);

internal sealed record CapacityVerdict(bool Admitted, long ProjectedCaptureBytes, long RequiredBytes, long DeficitBytes, string Summary);

/// <summary>
/// Whether the cache SSD can absorb a session if the server stops accepting
/// uploads: the projected recording, everything still waiting to upload, and
/// the reserve. Cache size limits are soft, so this is the real admission test.
/// </summary>
internal static class CapacityAdmission
{
    /// <summary>
    /// Recording admission for a configuration. The cache, and any persistent clip folder, must be
    /// connected, on a suitable volume and large enough. Volumes are compared by identity, so two
    /// letters for one disk share one budget and a disk mounted inside a folder is measured itself.
    /// </summary>
    public static IReadOnlyList<CapacityVerdict> ForRecording(MedalSyncConfig config, string cacheFolder, double bitrateMbps,
        long pendingUploadBytes, Func<string, VolumeInfo?> describe)
    {
        var cache = StorageVolume.RequirePresent(cacheFolder, "the SSD cache", describe);
        StorageVolume.RequireSuitable(StorageRole.Cache, config.CacheRoot, cache);
        var inputs = new CapacityInputs(cache.FreeBytes, (long)config.Mount.MinFreeGiB << 30, bitrateMbps,
            TimeSpan.FromHours(config.MaxSessionHours), pendingUploadBytes);
        if (config.PersistentMediaRoot is not { } persistent) return [Evaluate(inputs)];
        var media = StorageVolume.RequirePresent(persistent, "persistent clip storage", describe);
        StorageVolume.RequireSuitable(StorageRole.Media, persistent, media);
        return WithPersistentCopy(inputs, media.FreeBytes, StorageVolume.SameVolume(cache, media));
    }

    public static IReadOnlyList<CapacityVerdict> WithPersistentCopy(CapacityInputs cache, long persistentFreeBytes, bool sameVolume)
    {
        var cached = Evaluate(cache);
        // On one volume, cached writes and the persistent copy may occupy space at the same time.
        return sameVolume
            ? [Evaluate(cache with { PendingUploadBytes = checked(cache.PendingUploadBytes + cached.ProjectedCaptureBytes) })]
            : [cached, Evaluate(cache with { FreeBytes = persistentFreeBytes, PendingUploadBytes = 0 })];
    }

    public static CapacityVerdict Evaluate(CapacityInputs inputs)
    {
        if (inputs.BitrateMbps <= 0 || double.IsNaN(inputs.BitrateMbps) || inputs.MaxSession <= TimeSpan.Zero)
        {
            throw new ArgumentException("Bitrate and session length must be positive.", nameof(inputs));
        }
        if (inputs.FreeBytes < 0 || inputs.ReserveBytes < 0 || inputs.PendingUploadBytes < 0)
        {
            throw new ArgumentException("Byte counts cannot be negative.", nameof(inputs));
        }
        // Megabits per second -> bytes: 1 Mb/s is 125,000 bytes per second.
        long projected = checked((long)Math.Ceiling(inputs.BitrateMbps * 125_000 * inputs.MaxSession.TotalSeconds));
        long required = checked(projected + inputs.PendingUploadBytes + inputs.ReserveBytes);
        long deficit = Math.Max(0, required - inputs.FreeBytes);
        string summary = deficit == 0
            ? $"OK: {Gb(inputs.FreeBytes)} free covers {Gb(projected)} of recording, {Gb(inputs.PendingUploadBytes)} pending and a {Gb(inputs.ReserveBytes)} reserve."
            : $"Not enough SSD space: {Gb(deficit)} short of {Gb(projected)} recording + {Gb(inputs.PendingUploadBytes)} pending + {Gb(inputs.ReserveBytes)} reserve.";
        return new CapacityVerdict(deficit == 0, projected, required, deficit, summary);
    }

    private static string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";
}

using System.Globalization;

namespace MedalSync.Storage;

internal enum SpaceLevel { Ok, Warning, Critical }

/// <summary>A folder whose disk is watched while recording, such as the SSD cache or the persistent clip folder.</summary>
internal sealed record WatchedFolder(string Label, string Path);

/// <summary>
/// Warns while Medal records when a watched disk runs low, without blocking supervision on a slow disk.
/// Only changes are announced: low, at the reserve, and back to normal. Hysteresis and a quiet period keep a disk that
/// hovers near a threshold from producing a stream of notices. A failed probe is reported as unknown, never as enough
/// space. It only warns: nothing is deleted and Medal is never closed.
/// </summary>
/// <param name="criticalBytes">The free-space reserve; at or below it Medal may fail to save clips.</param>
/// <param name="warningBytes">An early warning above the reserve.</param>
/// <param name="bitrateMbps">Medal's recording bitrate, for an approximate time left; null when unknown.</param>
internal sealed class SpacePressureMonitor(IReadOnlyList<WatchedFolder> folders, long warningBytes, long criticalBytes,
    Func<string, VolumeInfo?> describe, double? bitrateMbps = null, TimeSpan? probeTimeout = null)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>The same notice for a disk is not repeated sooner than this.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(30);

    /// <summary>Probes that fail in a row before saying the space cannot be checked; one slow answer is not a problem.</summary>
    public const int FailuresBeforeNotice = 3;

    private readonly TimeSpan _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, Disk> _disks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Probe> _probes = new(StringComparer.Ordinal);
    private DateTimeOffset? _nextCheck;

    private long Margin => Math.Max(1L << 30, warningBytes / 20);

    /// <summary>
    /// The defaults follow the configured reserve: critical at the reserve (at least 1 GiB), and a warning at twice that,
    /// but at least 10 GiB above it. <paramref name="warningGiB"/> overrides the warning level.
    /// </summary>
    public static (long Warning, long Critical) Thresholds(int reserveGiB, int? warningGiB)
    {
        long critical = Math.Max(1L << 30, (long)Math.Max(0, reserveGiB) << 30);
        long warning = warningGiB is { } configured ? (long)configured << 30 : Math.Max(2 * critical, critical + (10L << 30));
        return (Math.Max(warning, critical), critical);
    }

    /// <summary>Notices to show now; empty between checks and when nothing changed. Never throws.</summary>
    public IReadOnlyList<string> Check(DateTimeOffset now)
    {
        if (_nextCheck is { } next && now < next) return [];
        _nextCheck = now + CheckInterval;
        var notices = new List<string>();
        var seen = new List<(WatchedFolder Folder, VolumeInfo Volume)>();
        foreach (var folder in folders)
        {
            var probe = _probes.TryGetValue(folder.Path, out var existing) ? existing : _probes[folder.Path] = new Probe();
            var (volume, problem) = probe.Read(folder.Path, describe, _probeTimeout);
            if (volume is not null)
            {
                probe.Failures = 0;
                seen.Add((folder, volume));
                continue;
            }
            if (++probe.Failures == FailuresBeforeNotice && (probe.LastNotice is null || now - probe.LastNotice >= QuietPeriod))
            {
                probe.LastNotice = now;
                notices.Add($"Free space for the {folder.Label} could not be checked: {problem}. Recording continues; "
                    + "if clips stop saving, check that disk. Medal Sync never deletes clips that have not been copied yet.");
            }
        }
        foreach (var group in seen.GroupBy(s => s.Volume.Identity, StringComparer.OrdinalIgnoreCase))
        {
            var volume = group.First().Volume;
            string label = string.Join(" and ", group.Select(s => s.Folder.Label).Distinct(StringComparer.Ordinal));
            var disk = _disks.TryGetValue(volume.Identity, out var known) ? known : _disks[volume.Identity] = new Disk();
            if (Update(disk, volume, label, now) is { } notice) notices.Add(notice);
        }
        return notices;
    }

    private string? Update(Disk disk, VolumeInfo volume, string label, DateTimeOffset now)
    {
        long free = volume.FreeBytes;
        var previous = disk.Level;
        var level = previous switch
        {
            SpaceLevel.Critical when free <= criticalBytes + Margin => SpaceLevel.Critical,
            SpaceLevel.Critical or SpaceLevel.Warning when free <= criticalBytes => SpaceLevel.Critical,
            SpaceLevel.Critical or SpaceLevel.Warning when free <= warningBytes + Margin => SpaceLevel.Warning,
            _ => free <= criticalBytes ? SpaceLevel.Critical : free <= warningBytes ? SpaceLevel.Warning : SpaceLevel.Ok,
        };
        disk.Level = level;
        string where = $"{volume.MountPath} ({label})";
        if (level > previous)
        {
            // A repeated notice waits out the quiet period: a disk bouncing around either threshold says it once.
            // Reaching the reserve from plenty of space is always said at once.
            if (level == SpaceLevel.Warning && disk.LastWarning is { } last && now - last < QuietPeriod) return null;
            if (level == SpaceLevel.Critical && previous == SpaceLevel.Warning && disk.LastCritical is { } lastCritical && now - lastCritical < QuietPeriod)
                return null;
            if (level == SpaceLevel.Critical) disk.LastCritical = now;
            disk.LastWarning = now;
            disk.Announced = true;
            return level == SpaceLevel.Critical
                ? $"Disk space on {where} is at the {StorageVolume.Gb(criticalBytes)} reserve: {StorageVolume.Gb(free)} free. "
                    + "Medal may soon fail to save clips. Free up space on that disk now. Medal Sync never deletes clips that have not been copied yet."
                : $"Low disk space on {where}: {StorageVolume.Gb(free)} free, close to the {StorageVolume.Gb(criticalBytes)} reserve.{TimeLeft(free)} "
                    + "Free up space on that disk. Medal Sync never deletes clips that have not been copied yet.";
        }
        if (level == SpaceLevel.Ok && previous != SpaceLevel.Ok && disk.Announced)
        {
            disk.Announced = false;
            return $"Disk space on {where} is back to {StorageVolume.Gb(free)} free.";
        }
        return null;
    }

    private string TimeLeft(long free)
    {
        if (bitrateMbps is not { } bitrate || !double.IsFinite(bitrate) || bitrate <= 0) return "";
        double hours = (free - criticalBytes) / (bitrate * 125_000) / 3600;
        return hours <= 0 ? "" : string.Create(CultureInfo.InvariantCulture,
            $" At Medal's bitrate that is roughly {(hours < 1 ? $"{hours * 60:0} minutes" : $"{hours:0.#} hours")} of recording (approximate).");
    }

    private sealed class Disk
    {
        public SpaceLevel Level { get; set; }

        public DateTimeOffset? LastWarning { get; set; }

        public DateTimeOffset? LastCritical { get; set; }

        /// <summary>A low-space notice was shown and has not been followed by a recovery notice.</summary>
        public bool Announced { get; set; }
    }

    /// <summary>One folder's probe. A slow disk keeps its probe running in the background; no second one is started.</summary>
    private sealed class Probe
    {
        private Task<VolumeInfo?>? _running;

        public int Failures { get; set; }

        public DateTimeOffset? LastNotice { get; set; }

        public (VolumeInfo? Volume, string Problem) Read(string path, Func<string, VolumeInfo?> describe, TimeSpan timeout)
        {
            _running ??= Task.Run(() => describe(path));
            try
            {
                if (!_running.Wait(timeout)) return (null, "the disk did not answer in time");
            }
            catch (AggregateException)
            {
                // The probe failed; its exception is observed here and a new probe starts next time.
            }
            var finished = _running;
            _running = null;
            if (finished.IsFaulted) return (null, "the disk could not be read");
            return finished.Result is { } volume ? (volume, "") : (null, "its disk is not connected");
        }
    }
}

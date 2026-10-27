using System.Globalization;
using MedalSync.Util;

namespace MedalSync.Storage;

internal sealed record MountSettings(
    string Remote,
    char DriveLetter,
    string CacheDirectory,
    TimeSpan WriteBack,
    long CacheMaxBytes,
    long MinFreeBytes,
    TimeSpan CacheMaxAge,
    TimeSpan CachePollInterval,
    TimeSpan DirCacheTime,
    int Transfers,
    int ControlPort,
    string VolumeName,
    string LogFile);

/// <summary>
/// Builds the rclone mount command line. Credentials are never arguments:
/// the control API user and password are passed in the environment variables
/// rclone maps to <c>--rc-user</c> and <c>--rc-pass</c>.
/// </summary>
internal static class MountPlan
{
    public const string ControlUserVariable = "RCLONE_RC_USER";
    public const string ControlPasswordVariable = "RCLONE_RC_PASS";

    private const long MiB = 1024L * 1024;
    private const long GiB = 1024 * MiB;

    public static IReadOnlyList<string> Arguments(MountSettings settings)
    {
        Validate(settings);
        return
        [
            "mount", settings.Remote, $"{char.ToUpperInvariant(settings.DriveLetter)}:",
            "--vfs-cache-mode", "full",
            "--vfs-write-back", Duration(settings.WriteBack),
            "--vfs-cache-max-size", Size(settings.CacheMaxBytes),
            "--vfs-cache-min-free-space", Size(settings.MinFreeBytes),
            "--vfs-cache-max-age", Duration(settings.CacheMaxAge),
            "--vfs-cache-poll-interval", Duration(settings.CachePollInterval),
            "--dir-cache-time", Duration(settings.DirCacheTime),
            "--transfers", settings.Transfers.ToString(CultureInfo.InvariantCulture),
            "--cache-dir", settings.CacheDirectory,
            "--volname", settings.VolumeName,
            "--rc", "--rc-addr", $"127.0.0.1:{settings.ControlPort.ToString(CultureInfo.InvariantCulture)}",
            "--log-file", settings.LogFile,
            "--log-level", "INFO",
            "--no-console",
        ];
    }

    public static void Validate(MountSettings settings)
    {
        var problems = new List<string>();
        if (!WindowsPath.TryParse(settings.Remote, out var remote, out _)
            || (!remote.Root.StartsWith(@"\\", StringComparison.Ordinal) && remote.Segments.Count == 0))
        {
            problems.Add("the remote must be a UNC share folder or an absolute local storage folder");
        }
        char letter = char.ToUpperInvariant(settings.DriveLetter);
        if (remote is not null && !remote.Root.StartsWith(@"\\", StringComparison.Ordinal) && remote.Root[0] == letter)
            problems.Add("the storage folder cannot be on the mounted drive");
        if (letter is < 'D' or > 'Z') problems.Add("the drive letter must be D through Z");
        if (!WindowsPath.TryParse(settings.CacheDirectory, out var cache, out _) || cache.Root.StartsWith(@"\\", StringComparison.Ordinal)
            || cache.Root[0] == letter)
        {
            problems.Add("the cache directory must be on a local SSD, not the share or the mounted drive");
        }
        if (!WindowsPath.TryParse(settings.LogFile, out var log, out _) || log.Root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            problems.Add("the log file must be a local path");
        }
        foreach (var (name, value) in new[]
        {
            ("write-back", settings.WriteBack), ("cache max age", settings.CacheMaxAge),
            ("cache poll interval", settings.CachePollInterval), ("directory cache time", settings.DirCacheTime),
        })
        {
            if (value <= TimeSpan.Zero || value.Ticks % TimeSpan.TicksPerSecond != 0) problems.Add($"{name} must be a whole number of seconds");
        }
        if (settings.CacheMaxBytes < GiB || settings.MinFreeBytes < GiB) problems.Add("cache size and free-space reserve must be at least 1 GiB");
        if (settings.Transfers is < 1 or > 8) problems.Add("transfers must be 1 to 8");
        if (settings.ControlPort is < 1024 or > 65535) problems.Add("the control port must be 1024 to 65535");
        if (settings.VolumeName.Length is < 1 or > 32 || !settings.VolumeName.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_'))
        {
            problems.Add("the volume name must be 1-32 letters, digits, spaces, '-' or '_'");
        }
        if (problems.Count > 0) throw new ArgumentException("Invalid mount settings: " + string.Join("; ", problems) + ".", nameof(settings));
    }

    internal static string Duration(TimeSpan value) =>
        ((long)value.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";

    /// <summary>rclone reads a bare number as KiB, so a unit is always written.</summary>
    internal static string Size(long bytes) =>
        bytes % GiB == 0 ? (bytes / GiB).ToString(CultureInfo.InvariantCulture) + "Gi"
        : bytes % MiB == 0 ? (bytes / MiB).ToString(CultureInfo.InvariantCulture) + "Mi"
        : bytes.ToString(CultureInfo.InvariantCulture) + "B";
}

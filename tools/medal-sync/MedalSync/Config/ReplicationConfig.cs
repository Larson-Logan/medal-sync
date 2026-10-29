using System.Text.RegularExpressions;
using MedalSync.Util;

namespace MedalSync.Config;

/// <summary>A named rclone remote; credentials live only in the user's separate rclone config.</summary>
internal sealed record ReplicaTarget(string Name, string Remote)
{
    /// <summary>Required targets block handoff until verified; others are retried later when offline or failing.</summary>
    public bool Required { get; init; }
}

internal enum ReplicaConflictPolicy { Stop, Version }

internal sealed record ReplicationConfig(string? LocalCopyRoot, string? RcloneConfigPath, IReadOnlyList<ReplicaTarget> Targets)
{
    public int TimeoutMinutes { get; init; } = 240;
    public int? BandwidthLimitMiB { get; init; }
    public ReplicaConflictPolicy ConflictPolicy { get; init; } = ReplicaConflictPolicy.Stop;

    public IEnumerable<string> Problems(MedalSyncConfig config)
    {
        if (TimeoutMinutes is < 1 or > 1440) yield return "replication timeoutMinutes must be 1 to 1440";
        if (BandwidthLimitMiB is < 1 or > 10000) yield return "bandwidthLimitMiB must be 1 to 10000, or null for unlimited";
        if (!Enum.IsDefined(ConflictPolicy)) yield return "replication conflictPolicy is not supported";
        WindowsPath.TryParse(config.CaptureFolder, out var capture, out _);
        if (Targets is null) { yield return "replication targets must be an array"; yield break; }
        if (Targets.Count > 8) yield return "at most 8 replication targets are supported";
        if (LocalCopyRoot is not null)
        {
            if (!WindowsPath.TryParse(LocalCopyRoot, out var local, out _) || local.Root.StartsWith(@"\\", StringComparison.Ordinal)
                || local.Segments.Count == 0 || local.Root == capture?.Root)
                yield return "localCopyRoot must be a dedicated local folder outside the mounted drive";
            else
            {
                foreach (string other in new[] { config.CacheRoot, config.CoordinationRoot, config.Remote })
                    if (WindowsPath.TryParse(other, out var path, out _) && (path.IsWithin(local) || local.IsWithin(path)))
                        yield return "localCopyRoot must not overlap the cache, coordination folder or server storage";
            }
        }
        if (Targets.Count > 0 || config.PrimaryIsRclone)
        {
            if (RcloneConfigPath is null || !WindowsPath.TryParse(Environment.ExpandEnvironmentVariables(RcloneConfigPath), out var credentials, out _)
                || credentials.Root.StartsWith(@"\\", StringComparison.Ordinal) || credentials.Root == capture?.Root)
                yield return "cloud targets require an explicit local rcloneConfigPath outside the mounted drive";
            else
                foreach (string root in new[] { config.CacheRoot, config.MountRemote, config.CoordinationRoot })
                    if (WindowsPath.TryParse(root, out var folder, out _) && credentials.IsWithin(folder))
                        yield return "rclone credentials must stay outside media, cache and coordination folders";
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new List<string>();
        string? primary = config.PrimaryIsRclone ? config.Remote.TrimEnd('/') : null;
        foreach (var target in Targets)
        {
            if (target is null) { yield return "replication targets cannot contain null"; continue; }
            if (string.IsNullOrEmpty(target.Name) || !Regex.IsMatch(target.Name, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,31}\z") || !names.Add(target.Name))
                yield return "replica names must be unique, with 1-32 letters, digits, '-' or '_'";
            if (!ValidRemote(target.Remote)) yield return "replica remote must be a named rclone remote and dedicated folder, such as onedrive:MedalSync";
            else
            {
                string destination = target.Remote.TrimEnd('/');
                if (destinations.Any(d => destination.Equals(d, StringComparison.OrdinalIgnoreCase)
                    || destination.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) || d.StartsWith(destination + "/", StringComparison.OrdinalIgnoreCase)))
                    yield return "replica destinations must not overlap";
                else if (primary is not null && (destination.Equals(primary, StringComparison.OrdinalIgnoreCase)
                    || destination.StartsWith(primary + "/", StringComparison.OrdinalIgnoreCase) || primary.StartsWith(destination + "/", StringComparison.OrdinalIgnoreCase)))
                    yield return "replica destinations must not overlap primary storage";
                destinations.Add(destination);
            }
        }
    }

    /// <summary>
    /// A named rclone remote and a dedicated folder. Names follow rclone's own rules (letters in any
    /// language, digits, '_', '-', '.', '+', '@' and inner spaces), but must start with a letter, digit
    /// or '_' and have two or more characters, so a value is never read as a flag or a Windows drive
    /// letter. The folder may start with '/' for an absolute path, as an SFTP or FTP server needs for
    /// a folder outside the account's home directory.
    /// </summary>
    internal static bool ValidRemote(string? remote)
    {
        if (remote is null || !Regex.IsMatch(remote, @"\A[\p{L}\p{N}_][\p{L}\p{N}_.+@ -]*[\p{L}\p{N}_.+@-]:[^:]+\z") || remote.Any(char.IsControl)
            || remote.Contains('\\') || remote.Contains('"')) return false;
        string folder = remote[(remote.IndexOf(':') + 1)..].TrimEnd('/');
        if (folder.StartsWith('/')) folder = folder[1..];
        return folder.Length > 0 && folder.Split('/').All(s => !string.IsNullOrWhiteSpace(s) && s is not "." and not "..");
    }
}

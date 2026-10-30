using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Util;

namespace MedalSync.Storage;

internal static class ReplicationPlan
{
    /// <summary>Only known media references; never database backups, credentials or arbitrary trees.</summary>
    public static IReadOnlyList<string> Files(LibrarySnapshot snapshot, WindowsPath capture, WindowsPath source, IFileProbe probe)
    {
        var files = new List<string>();
        foreach (var (relative, required) in Media(snapshot, capture))
        {
            var found = probe.Probe(capture.Append(relative.Split('/')).Rebase(capture, source), source);
            if (found.HasReparsePoint || found.IsDirectory || (!found.Exists && required))
                throw new IOException("Referenced media is missing or unsafe; replication was refused.");
            if (found.Exists) files.Add(relative);
        }
        return files;
    }

    /// <summary>Every safe, known media reference relative to the capture folder, with whether the library requires it.</summary>
    public static IReadOnlyList<(string Relative, bool Required)> Media(LibrarySnapshot snapshot, WindowsPath capture)
    {
        var files = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in AssetInventory.Collect(snapshot).Where(r => r.Role != AssetRole.Unrecognized))
        {
            if (!WindowsPath.TryParse(reference.Path, out var path, out _) || !path.IsWithin(capture) || path.Equals(capture))
                throw new UnsafeOperationException("A media reference is outside the capture folder; replication was refused.");
            string relative = string.Join('/', path.Segments.Skip(capture.Segments.Count));
            string extension = Path.GetExtension(relative).ToLowerInvariant();
            bool overlay = reference is { Role: AssetRole.Optional, Field: "metadata:$.keyboardOverlayFilePath" } && extension == ".json";
            if (relative.Split('/').Any(s => s.Equals("Databases", StringComparison.OrdinalIgnoreCase) || s.Equals(".medal-sync-history", StringComparison.OrdinalIgnoreCase))
                || (!overlay && extension is not (".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" or ".m4v"
                    or ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".avif" or ".heic")))
                throw new UnsafeOperationException("A library reference points to private state instead of media; replication was refused.");
            bool required = reference.Role == AssetRole.Required;
            files[relative] = files.TryGetValue(relative, out bool already) ? already || required : required;
        }
        return files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (f.Key, f.Value)).ToArray();
    }

    public static IReadOnlyList<string> Arguments(string command, string source, string target, string fileList, string configPath, int? bandwidthLimitMiB = null, string? versionDirectory = null, bool sizeOnly = false)
    {
        if (command is not ("copy" or "check")) throw new ArgumentException("Unsupported replication command.", nameof(command));
        List<string> arguments = [command, source, target, "--config", configPath, "--files-from-raw", fileList,
            "--retries", "2", "--low-level-retries", "3", "--contimeout", "10s", "--timeout", "60s", "--transfers", "2"];
        // Immutable prevents silent replacement. Download verification compares bytes on backends without a common hash.
        if (command == "copy")
        {
            arguments.Add("--checksum");
            if (versionDirectory is null) arguments.Add("--immutable");
            else arguments.AddRange(["--backup-dir", versionDirectory]);
        }
        else arguments.AddRange(["--one-way", sizeOnly ? "--size-only" : "--download"]);
        if (bandwidthLimitMiB is { } bandwidth)
        {
            if (bandwidth is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(bandwidthLimitMiB));
            arguments.AddRange(["--bwlimit", bandwidth.ToString(System.Globalization.CultureInfo.InvariantCulture) + "Mi"]);
        }
        return arguments;
    }

    public static string Destination(ReplicaTarget target, string libraryId) => target.Remote.TrimEnd('/') + "/" + libraryId;
}

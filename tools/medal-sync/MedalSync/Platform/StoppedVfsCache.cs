using System.Text.Json;

namespace MedalSync.Platform;

/// <summary>
/// Reads a stopped clip drive's rclone VFS cache to tell unsent writes from read copies.
/// rclone keeps <c>vfs/&lt;remote&gt;/&lt;path&gt;</c> (data) beside <c>vfsMeta/&lt;remote&gt;/&lt;path&gt;</c> (state).
/// It marks an item <c>"Dirty": true</c> before caching its first write and clears the flag once the upload
/// succeeds, and a restarted drive re-uploads exactly the dirty items. Files that were only read, such as
/// the library marker that every health check reads, stay <c>"Dirty": false</c>. Anything this class
/// cannot prove clean counts as unsent.
/// </summary>
internal static class StoppedVfsCache
{
    private const string Data = "vfs";
    private const string Metadata = "vfsMeta";
    private const long MaxMetadataBytes = 1 << 20;
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>True only when every cached file is a clean copy of storage, or the cache holds no files.</summary>
    public static bool HoldsNoUnsentFiles(string cache)
    {
        if (AbortedStartRecovery.CacheIsEmptyOrAbsent(cache, allowEmptyDirectories: true)) return true;
        try
        {
            cache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cache));
            for (var directory = new DirectoryInfo(cache); directory is not null; directory = directory.Parent)
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            var clean = new HashSet<string>(Paths);
            foreach (string entry in Directory.EnumerateFileSystemEntries(cache))
            {
                string name = Path.GetFileName(entry);
                if (!IsPlainDirectory(entry)) return false;
                if (Paths.Equals(name, Metadata)) { if (!AllClean(entry, entry, clean)) return false; }
                else if (!Paths.Equals(name, Data)) return false;
            }
            string data = Path.Combine(cache, Data);
            return !Directory.Exists(data) || AllDescribed(data, data, clean);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Every metadata file says Dirty is false; their paths, relative to <c>vfsMeta</c>, are collected.</summary>
    private static bool AllClean(string root, string directory, HashSet<string> clean)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (IsPlainDirectory(entry))
            {
                if (!AllClean(root, entry, clean)) return false;
                continue;
            }
            if (!IsPlainFile(entry) || !IsCleanMetadata(entry)) return false;
            clean.Add(Path.GetRelativePath(root, entry));
        }
        return true;
    }

    /// <summary>Every data file has clean metadata at the same relative path.</summary>
    private static bool AllDescribed(string root, string directory, HashSet<string> clean)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (IsPlainDirectory(entry))
            {
                if (!AllDescribed(root, entry, clean)) return false;
                continue;
            }
            if (!IsPlainFile(entry) || !clean.Contains(Path.GetRelativePath(root, entry))) return false;
        }
        return true;
    }

    internal static bool IsCleanMetadata(string file)
    {
        if (new FileInfo(file).Length > MaxMetadataBytes) return false;
        using var document = JsonDocument.Parse(File.ReadAllBytes(file));
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("Dirty", out var dirty)
            && dirty.ValueKind == JsonValueKind.False;
    }

    private static bool IsPlainDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool IsPlainFile(string path)
    {
        var attributes = File.GetAttributes(path);
        return !attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}

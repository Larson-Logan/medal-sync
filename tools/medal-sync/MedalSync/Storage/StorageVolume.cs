using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using MedalSync.Config;

namespace MedalSync.Storage;

internal enum VolumeKind { Unknown, Fixed, Removable, Network, RamDisk, Optical }

/// <summary>What a folder is stored on.</summary>
internal enum StorageRole
{
    /// <summary>The rclone cache, which holds new recordings until they upload.</summary>
    Cache,

    /// <summary>A persistent clip collection: <c>localCopyRoot</c>, or local mode's storage folder.</summary>
    Media,
}

/// <summary>
/// The volume that actually holds a folder. A drive letter is not enough: a disk
/// can be mounted inside a folder on another drive, a subst letter can alias a
/// folder on another drive, and a mapped letter can be a network share.
/// </summary>
/// <param name="MountPath">Where the volume is reached, such as <c>D:\</c>, <c>C:\Disks\Games\</c> or <c>\\server\share\</c>.</param>
/// <param name="Identity">Equal for two folders on the same volume, whichever letter or folder reaches them.</param>
/// <param name="FreeBytes">Space this user can still write, after any disk quota.</param>
internal sealed record VolumeInfo(string MountPath, string Identity, VolumeKind Kind, string? FileSystem, long FreeBytes, long TotalBytes)
{
    /// <summary>FAT12/16/32 store at most 4 GiB minus one byte per file. exFAT has no such limit.</summary>
    public bool IsFat => FileSystem is { } name
        && (name.ToUpperInvariant() is "FAT" or "FAT12" or "FAT16" or "FAT32" or "VFAT" or "MSDOS");
}

/// <summary>The volume that really holds a configured folder, and why it cannot be used, if it cannot.</summary>
internal sealed record StorageVolumeSummary(string Role, string Folder, string? Volume, string? FileSystem, string? Kind, double? FreeGb,
    IReadOnlyList<string> Problems);

internal static class StorageVolume
{
    /// <summary>
    /// Describes the volume holding <paramref name="path"/>, using its nearest existing
    /// folder so a folder that is about to be created is measured on the right disk.
    /// Null when not even the drive or share exists, for example an unplugged USB disk.
    /// </summary>
    public static VolumeInfo? Describe(string path)
    {
        string? folder = NearestExistingFolder(path);
        if (folder is null) return null;
        return OperatingSystem.IsWindows() ? DescribeWindows(folder) : DescribePortable(folder);
    }

    internal static string? NearestExistingFolder(string path)
    {
        string? current;
        try { current = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        for (; current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current)) return current;
        return null;
    }

    public static bool SameVolume(VolumeInfo first, VolumeInfo second) =>
        string.Equals(first.Identity, second.Identity, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="volume"/> cannot hold <paramref name="folder"/> for <paramref name="role"/>; empty when it can.</summary>
    public static IReadOnlyList<string> Problems(StorageRole role, string folder, VolumeInfo volume)
    {
        string what = role == StorageRole.Cache ? "The SSD cache folder" : "The persistent clip folder";
        var problems = new List<string>();
        switch (volume.Kind)
        {
            case VolumeKind.Network when role == StorageRole.Cache:
                problems.Add($"{what} {folder} is on a network drive ({volume.MountPath}). Medal records into the cache first, so choose a disk inside this PC.");
                break;
            case VolumeKind.RamDisk:
                problems.Add($"{what} {folder} is on a RAM disk ({volume.MountPath}), which loses clips that have not been copied yet when Windows restarts. Choose a disk inside this PC.");
                break;
            case VolumeKind.Optical:
                problems.Add($"{what} {folder} is on an optical drive ({volume.MountPath}). Choose a writable disk inside this PC.");
                break;
        }
        if (volume.IsFat)
        {
            problems.Add($"{what} {folder} is on a {volume.FileSystem} volume ({volume.MountPath}), which cannot store files of 4 GiB or more, "
                + "and long recordings can be larger. Choose an NTFS, ReFS or exFAT volume.");
        }
        return problems;
    }

    /// <summary>The cache and any persistent clip folder, measured on the volumes that really hold them.</summary>
    public static IReadOnlyList<StorageVolumeSummary> Summaries(MedalSyncConfig? config, Func<string, VolumeInfo?>? describe = null)
    {
        if (config is null) return [];
        describe ??= Describe;
        var folders = new List<(StorageRole Role, string Name, string Folder)> { (StorageRole.Cache, "cache", config.CacheRoot) };
        if (config.PersistentMediaRoot is { } media) folders.Add((StorageRole.Media, "persistent clips", media));
        var summaries = new List<StorageVolumeSummary>();
        foreach (var (role, name, folder) in folders)
        {
            VolumeInfo? volume;
            try { volume = describe(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                summaries.Add(new(name, folder, null, null, null, null, [$"The volume holding {folder} cannot be read: {ex.Message}"]));
                continue;
            }
            summaries.Add(volume is null
                ? new(name, folder, null, null, null, null, [$"The drive for {folder} ({DriveName(folder)}) is not connected."])
                : new(name, folder, volume.MountPath, volume.FileSystem, volume.Kind.ToString(), Math.Round(volume.FreeBytes / 1e9, 1),
                    Problems(role, folder, volume)));
        }
        return summaries;
    }

    /// <summary>Throws with every problem when the volume cannot hold the folder.</summary>
    public static void RequireSuitable(StorageRole role, string folder, VolumeInfo volume)
    {
        var problems = Problems(role, folder, volume);
        if (problems.Count > 0) throw new UnsafeOperationException(string.Join(" ", problems) + " Nothing was changed.");
    }

    /// <summary>The volume for a folder that must be present now, such as the cache before recording.</summary>
    public static VolumeInfo RequirePresent(string folder, string what, Func<string, VolumeInfo?> describe) =>
        describe(folder) ?? throw new StorageOfflineException(
            $"The drive for {what} ({DriveName(folder)}) is not connected. Reconnect it and open Medal again; nothing was changed.");

    /// <summary>The drive letter or share named by a path, for messages.</summary>
    internal static string DriveName(string path)
    {
        try { return Path.GetPathRoot(path) is { Length: > 0 } root ? root : path; }
        catch (ArgumentException) { return path; }
    }

    internal static string Gb(long bytes) => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    private static VolumeInfo DescribeWindows(string folder)
    {
        string directory = folder.EndsWith('\\') ? folder : folder + "\\";
        var buffer = new char[32768];
        string mount = GetVolumePathNameW(directory, buffer, (uint)buffer.Length) ? Terminated(buffer) : Path.GetPathRoot(directory) ?? directory;
        if (!mount.EndsWith('\\')) mount += "\\";
        var kind = GetDriveTypeW(mount) switch
        {
            2 => VolumeKind.Removable,
            3 => VolumeKind.Fixed,
            4 => VolumeKind.Network,
            5 => VolumeKind.Optical,
            6 => VolumeKind.RamDisk,
            _ => VolumeKind.Unknown,
        };
        string? fileSystem = null;
        string identity = mount.ToUpperInvariant();
        var name = new char[261];
        if (GetVolumeInformationW(mount, null, 0, out uint serial, out _, out _, name, (uint)name.Length))
        {
            fileSystem = Terminated(name);
            // The serial follows the volume through subst letters, mounted folders and extra drive letters.
            // Two volumes rarely share one (cloned disks); then both copies are budgeted together, never apart.
            if (serial != 0) identity = fileSystem.ToUpperInvariant() + ":" + serial.ToString("X8", CultureInfo.InvariantCulture);
        }
        if (!GetDiskFreeSpaceExW(directory, out ulong free, out ulong total, out _))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"Free space on {mount} cannot be read: {new Win32Exception(error).Message}");
        }
        return new VolumeInfo(mount, identity, kind, fileSystem, (long)Math.Min(free, long.MaxValue), (long)Math.Min(total, long.MaxValue));
    }

    /// <summary>Lets the same checks run on test machines that are not Windows.</summary>
    private static VolumeInfo DescribePortable(string folder)
    {
        DriveInfo? best = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root = drive.RootDirectory.FullName;
            bool contains = folder == root || folder.StartsWith(root.EndsWith('/') ? root : root + "/", StringComparison.Ordinal);
            if (contains && (best is null || root.Length > best.RootDirectory.FullName.Length)) best = drive;
        }
        if (best is null) throw new IOException($"No mounted volume holds {folder}.");
        var kind = best.DriveType switch
        {
            DriveType.Fixed => VolumeKind.Fixed,
            DriveType.Removable => VolumeKind.Removable,
            DriveType.Network => VolumeKind.Network,
            DriveType.Ram => VolumeKind.RamDisk,
            DriveType.CDRom => VolumeKind.Optical,
            _ => VolumeKind.Unknown,
        };
        string mount = best.RootDirectory.FullName;
        return new VolumeInfo(mount, mount, kind, best.DriveFormat, best.AvailableFreeSpace, best.TotalSize);
    }

    private static string Terminated(char[] buffer)
    {
        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPathName, [Out] char[]? volumeName, uint volumeNameSize,
        out uint serialNumber, out uint maximumComponentLength, out uint fileSystemFlags, [Out] char[] fileSystemName, uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeAvailable, out ulong total, out ulong totalFree);
}

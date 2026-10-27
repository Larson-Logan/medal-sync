using System.Text;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Util;

namespace MedalSync.Storage;

internal static class StorageIdentity
{
    /// <summary>Creates fresh storage, or verifies its existing marker without replacing it.</summary>
    public static void Initialize(string captureDirectory, string libraryId)
    {
        if (!CoordinationStore.IsValidId(libraryId)) throw new ArgumentException("Invalid library ID.", nameof(libraryId));
        for (DirectoryInfo? directory = new(captureDirectory); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnsafeOperationException("Storage initialization cannot follow a directory link.");
        Directory.CreateDirectory(captureDirectory);
        string marker = Path.Combine(captureDirectory, MedalSyncConfig.StorageMarker);
        if (File.Exists(marker))
        {
            if (File.ReadAllText(marker).Trim() != libraryId) throw new UnsafeOperationException("Storage already names a different library.");
            return;
        }
        AtomicFile.CreateNew(marker, Encoding.UTF8.GetBytes(libraryId + "\n"));
    }
}

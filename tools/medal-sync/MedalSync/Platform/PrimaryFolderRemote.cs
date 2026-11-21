using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>
/// Cleanup operations on a share or local folder primary, through the file system. Links and junctions are refused
/// rather than followed, and a move never replaces a file, so a review can only ever move the exact files it listed.
/// </summary>
internal sealed class PrimaryFolderRemote(Action requireIdle) : IReplicaRemote
{
    public IReadOnlyList<RemoteFile> List(string destination)
    {
        requireIdle();
        var root = new DirectoryInfo(destination);
        if (!root.Exists) return [];
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Primary storage's clip folder is a link or junction; cleanup was refused.");
        var files = new List<RemoteFile>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>([root]);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var entry in folder.EnumerateFileSystemInfos("*", new EnumerationOptions
                { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false }))
            {
                string relative = Path.GetRelativePath(root.FullName, entry.FullName).Replace('\\', '/');
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Primary storage contains a link or junction; cleanup was refused. Remove the link, then review again.");
                if (!identities.Add(relative))
                    throw new InvalidDataException("Primary storage lists duplicate or case-conflicting paths; cleanup was refused.");
                if (entry is DirectoryInfo directory) pending.Push(directory);
                else if (entry is FileInfo file)
                    files.Add(new(relative, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    public RemoteCheckOutcome Check(string source, string destination, IReadOnlyList<string> files, Action? guard = null) =>
        throw new NotSupportedException("Primary storage is verified by finish, not by cleanup.");

    public void Move(string destination, IReadOnlyList<string> files, string folder)
    {
        RequireRelative(folder);
        foreach (string file in files)
        {
            requireIdle();
            RequireRelative(file);
            string from = Inside(destination, file);
            string to = Inside(destination, folder + "/" + file);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            // Never replaces: a file already in the batch means the listing changed, which the caller refuses.
            File.Move(from, to, overwrite: false);
        }
    }

    public void Mark(string destination, string folder, string name)
    {
        RequireRelative(folder);
        RequireRelative(name);
        string path = Inside(destination, folder + "/" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) AtomicFile.CreateNew(path, []);
    }

    public void Purge(string destination, string folder)
    {
        RequireRelative(folder);
        requireIdle();
        string path = Inside(destination, folder);
        if (!Directory.Exists(path)) return;
        // Deleting a junction removes the link, never its target, but the trash should hold none: refuse it.
        if (new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(e => e.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidDataException("A trash batch contains a link or junction; it was not deleted.");
        Directory.Delete(path, recursive: true);
    }

    private static string Inside(string destination, string relative)
    {
        string root = Path.GetFullPath(destination);
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected a path inside primary storage.", nameof(relative));
        return path;
    }

    private static void RequireRelative(string path)
    {
        if (path.Length == 0 || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(s => s is "" or "." or ".."))
            throw new ArgumentException("Expected a relative path inside primary storage.", nameof(path));
    }
}

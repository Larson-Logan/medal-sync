namespace MedalSync.Util;

/// <summary>
/// Durable writes for the coordination directory and local state. Every write
/// goes to a temporary sibling, is flushed to the device, and is then renamed
/// into place, so a reader sees either the old file or the complete new one.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Replaces <paramref name="path"/> with <paramref name="bytes"/> atomically.</summary>
    public static void Replace(string path, ReadOnlySpan<byte> bytes)
    {
        string temp = TempSibling(path);
        try
        {
            WriteFlushed(temp, bytes, FileMode.CreateNew);
            MoveOver(temp, path);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="destination"/>. Antivirus scanners, search
    /// indexers and backup or sync clients briefly open files on local disks and NAS shares alike, and
    /// Windows then refuses the rename. That refusal is retried for about a second and a half; any
    /// other failure, or one that persists, is reported unchanged.
    /// </summary>
    internal static void MoveOver(string source, string destination, int attempts = 8, Action<TimeSpan>? sleep = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts && IsTransientLock(ex) && File.Exists(source))
            {
                (sleep ?? Thread.Sleep)(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    /// <summary>
    /// ERROR_ACCESS_DENIED (the destination is open without delete sharing), ERROR_SHARING_VIOLATION
    /// or ERROR_LOCK_VIOLATION.
    /// </summary>
    internal static bool IsTransientLock(Exception ex) => ex switch
    {
        UnauthorizedAccessException => true,
        IOException io => (io.HResult & 0xFFFF) is 32 or 33,
        _ => false,
    };

    /// <summary>Creates a new file; fails if <paramref name="path"/> already exists.</summary>
    public static void CreateNew(string path, ReadOnlySpan<byte> bytes) =>
        WriteFlushed(path, bytes, FileMode.CreateNew);

    private static void WriteFlushed(string path, ReadOnlySpan<byte> bytes, FileMode mode)
    {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string TempSibling(string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException($"'{path}' has no parent directory.", nameof(path));
        return Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file is harmless; readers never open it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

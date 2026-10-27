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
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

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

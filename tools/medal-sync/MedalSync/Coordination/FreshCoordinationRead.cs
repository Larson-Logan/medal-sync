using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MedalSync.Util;

namespace MedalSync.Coordination;

/// <summary>
/// The share returned an out-of-date copy of a coordination file. Nothing was decided from it, and nothing was changed.
/// </summary>
internal sealed class StaleCoordinationReadException(string message) : IOException(message);

/// <summary>
/// Reads a small coordination file and checks it against the server's directory listing.
/// <para>
/// Windows keeps a cached open of a file on an SMB share after it is closed and reuses it for the next open of the
/// same path. If the server never tells this PC that another PC replaced the file, every read returns the old bytes
/// for as long as something on this PC keeps opening it, and opens that cannot reuse it fail. A PC then kept seeing
/// the other PC's finished session as in use. The directory listing does not come from that cached open, so a listed
/// file newer than the bytes read proves the bytes are stale.
/// </para>
/// <para>
/// Then every Medal Sync process of this user stops opening the file for a while, so Windows drops the cached open
/// and the next read fetches the file from the server. A listing that is older than the bytes is only Windows'
/// short directory cache and is ignored.
/// </para>
/// </summary>
internal sealed class FreshCoordinationRead
{
    internal const int Attempts = 3;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(5);

    public static FreshCoordinationRead Default { get; } = new();

    /// <summary>The bytes and the last-write time reported by the same open.</summary>
    internal Func<string, (byte[] Bytes, DateTime WrittenUtc)> ReadOpen { get; init; } = ReadThroughOpen;

    /// <summary>The file's last-write time in its directory listing, or null when it is not listed.</summary>
    internal Func<string, DateTime?> ReadListing { get; init; } = ListedWriteTime;

    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
    internal Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>Shared by this user's Medal Sync processes, so the tray and a waiting launcher pause together.</summary>
    internal string PauseDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "medal-sync-stale-reads");

    public byte[] Read(string path, string what)
    {
        string marker = MarkerPath(path);
        var pause = ReadPause(marker);
        if (pause is { } paused && Now() < paused.Until) throw Stale(what, paused.Until);
        for (int attempt = 1; ; attempt++)
        {
            var (bytes, written) = ReadOpen(path);
            // Read after the open: a file replaced in between is newer, so it is retried rather than trusted.
            var listed = ReadListing(path);
            if (listed is not { } listedAt || listedAt <= written)
            {
                if (pause is not null) TryDelete(marker);
                return bytes;
            }
            if (attempt >= Attempts) break;
            // A file replaced while it was being read: the next open normally sees the new file.
            Sleep(RetryDelay);
        }
        var length = pause is null ? FirstPause : TimeSpan.FromTicks(Math.Min(pause.Value.Length.Ticks * 2, MaxPause.Ticks));
        var until = Now() + length;
        WritePause(marker, until, length);
        throw Stale(what, until);
    }

    private static StaleCoordinationReadException Stale(string what, DateTimeOffset until) => new(
        $"The server keeps returning an out-of-date copy of the {what}, so Medal Sync is not using it. Windows is "
        + $"still serving an older version that another PC has replaced. Medal Sync stops reading it until "
        + $"{until.ToLocalTime():HH:mm:ss} so Windows fetches it again. Nothing was changed.");

    private static (byte[] Bytes, DateTime WrittenUtc) ReadThroughOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return (bytes, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
    }

    private static DateTime? ListedWriteTime(string path)
    {
        string name = Path.GetFileName(path);
        // Entries from an enumeration keep the listing's data; FileInfo(path) would ask the cached open instead.
        foreach (var entry in new DirectoryInfo(Path.GetDirectoryName(path)!).EnumerateFiles(name))
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return entry.LastWriteTimeUtc;
        }
        return null;
    }

    private string MarkerPath(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return Path.Combine(PauseDirectory, Convert.ToHexString(hash, 0, 16) + ".pause");
    }

    private static (DateTimeOffset Until, TimeSpan Length)? ReadPause(string marker)
    {
        try
        {
            if (!File.Exists(marker)) return null;
            string[] parts = File.ReadAllText(marker).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long until)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long length)
                && until > 0 && length > 0 && length <= MaxPause.Ticks
                ? (new DateTimeOffset(until, TimeSpan.Zero), TimeSpan.FromTicks(length))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void WritePause(string marker, DateTimeOffset until, TimeSpan length)
    {
        // Advisory: without the marker this process still refuses the stale bytes; other processes find out themselves.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            AtomicFile.Replace(marker, Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{until.UtcTicks} {length.Ticks}")), durable: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string marker)
    {
        try { File.Delete(marker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

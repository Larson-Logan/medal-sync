namespace MedalSync.Storage;

/// <summary>Two unchanged observations, then a read lease that excludes writers for the entire copy.</summary>
internal sealed class SettledMedia
{
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, (long Size, DateTime Modified, DateTimeOffset Since)> _seen = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable? TryLease(string path, DateTimeOffset now)
    {
        FileStream? lease = null;
        try
        {
            var info = new FileInfo(path);
            var stamp = (info.Length, info.LastWriteTimeUtc);
            if (!_seen.TryGetValue(path, out var prior) || prior.Size != stamp.Length || prior.Modified != stamp.LastWriteTimeUtc)
            {
                _seen[path] = (stamp.Length, stamp.LastWriteTimeUtc, now);
                return null;
            }
            if (now - prior.Since < SettleTime || now.UtcDateTime - stamp.LastWriteTimeUtc < SettleTime) return null;
            // FileShare.Read permits rclone's reader, but refuses an existing or new writer/deleter.
            lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            info.Refresh();
            if (lease.Length == prior.Size && info.LastWriteTimeUtc == prior.Modified)
            {
                var ready = lease;
                lease = null;
                return ready;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Still writing, missing or inaccessible. */ }
        finally { lease?.Dispose(); }
        _seen.Remove(path);
        return null;
    }
}

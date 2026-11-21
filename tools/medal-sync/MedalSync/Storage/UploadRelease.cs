namespace MedalSync.Storage;

/// <summary>
/// Which held uploads to release while the clip drive holds Medal's replay buffer. While recording, the drive is
/// mounted with a long write-back delay (<c>mount.recordingBufferHoldSeconds</c>); every queued file outside
/// <c>Temp/RecordingBuffer</c> is released once it has waited the normal <c>mount.writeBackSeconds</c>, so saved clips
/// upload as quickly as before. Buffer segments stay queued until Medal deletes them, which drops them from the queue.
/// Anything this cannot classify (no name, no expiry) is released: a doubt falls back to today's behavior.
/// </summary>
internal static class UploadRelease
{
    /// <summary>The replay-buffer folder as rclone names queued files: relative to the drive root, with '/' separators.</summary>
    public static string BufferPrefix(Util.WindowsPath capture) =>
        string.Join('/', capture.Segments.Append("Temp").Append("RecordingBuffer")) + "/";

    /// <summary>Queue IDs to release now. With <paramref name="includeBuffer"/>, every waiting file is released.</summary>
    public static IReadOnlyList<ulong> Due(IEnumerable<UploadQueueItem> queue, string bufferPrefix, bool includeBuffer,
        TimeSpan hold, TimeSpan writeBack)
    {
        var due = new List<ulong>();
        foreach (var item in queue)
        {
            // A running upload cannot be changed; one already eligible needs nothing.
            if (item.Uploading || item.Expiry is <= 0) continue;
            if (includeBuffer || item.Name is null || item.Expiry is null) { due.Add(item.Id); continue; }
            if (InBuffer(item.Name, bufferPrefix)) continue;
            // rclone counts the delay from when the file was closed: it has waited (hold - expiry) seconds so far.
            if (hold.TotalSeconds - item.Expiry.Value >= writeBack.TotalSeconds) due.Add(item.Id);
        }
        return due;
    }

    /// <summary>Inside the buffer folder itself, not a sibling such as <c>Temp/RecordingBufferOld</c>.</summary>
    internal static bool InBuffer(string name, string bufferPrefix)
    {
        string normalized = name.Replace('\\', '/').TrimStart('/');
        return normalized.Length > bufferPrefix.Length && normalized.StartsWith(bufferPrefix, StringComparison.OrdinalIgnoreCase);
    }
}

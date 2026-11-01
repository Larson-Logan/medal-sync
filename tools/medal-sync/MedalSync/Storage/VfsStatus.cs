using System.Text.Json;

namespace MedalSync.Storage;

internal enum DrainState
{
    /// <summary>Nothing queued, uploading or failed.</summary>
    Drained,

    /// <summary>Uploads remain; wait.</summary>
    Pending,

    /// <summary>Upload errors or a full cache; the user must act. Dirty data stays local.</summary>
    Failed,

    /// <summary>The status could not be read or understood. Never treated as drained.</summary>
    Unknown,
}

internal sealed record UploadQueueItem(ulong Id, long Tries, bool Uploading);

internal sealed record DrainVerdict(DrainState State, IReadOnlyList<string> Reasons, int QueuedFiles, long QueuedBytes)
{
    public IReadOnlyList<UploadQueueItem> Uploads { get; init; } = [];
    public bool CacheSpaceTrouble { get; init; }
    public static DrainVerdict Unknown(string reason) => new(DrainState.Unknown, [reason], 0, 0);
}

/// <summary>
/// Interprets rclone's <c>vfs/stats</c> and <c>vfs/queue</c> answers. A missing
/// call, field or unexpected type is "unknown", never zero (rclone documents
/// that <c>diskCache</c> is absent when the cache mode is off).
/// </summary>
internal static class VfsStatus
{
    public static DrainVerdict Evaluate(string? statsJson, string? queueJson, string expectedFs)
    {
        if (statsJson is null) return DrainVerdict.Unknown("rclone did not answer vfs/stats.");
        if (queueJson is null) return DrainVerdict.Unknown("rclone did not answer vfs/queue.");
        try
        {
            using var stats = JsonDocument.Parse(statsJson);
            using var queue = JsonDocument.Parse(queueJson);
            return Evaluate(stats.RootElement, queue.RootElement, expectedFs);
        }
        catch (JsonException ex)
        {
            return DrainVerdict.Unknown($"rclone returned unreadable status: {ex.Message}");
        }
    }

    private static DrainVerdict Evaluate(JsonElement stats, JsonElement queue, string expectedFs)
    {
        if (stats.ValueKind != JsonValueKind.Object) return DrainVerdict.Unknown("vfs/stats is not an object.");
        if (!stats.TryGetProperty("fs", out var fs) || fs.ValueKind != JsonValueKind.String)
        {
            return DrainVerdict.Unknown("vfs/stats does not name its remote.");
        }
        if (!SameRemote(fs.GetString()!, expectedFs))
        {
            return DrainVerdict.Unknown($"The mount serves '{fs.GetString()}', not '{expectedFs}'.");
        }
        if (!stats.TryGetProperty("diskCache", out var cache) || cache.ValueKind != JsonValueKind.Object)
        {
            return DrainVerdict.Unknown("vfs/stats has no diskCache; the mount is not using --vfs-cache-mode full.");
        }
        if (!Integer(cache, "uploadsInProgress", out long inProgress)
            || !Integer(cache, "uploadsQueued", out long queued)
            || !Integer(cache, "erroredFiles", out long errored)
            || !cache.TryGetProperty("outOfSpace", out var outOfSpace) || outOfSpace.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return DrainVerdict.Unknown("vfs/stats lacks an upload counter this tool requires.");
        }
        if (queue.ValueKind != JsonValueKind.Object || !queue.TryGetProperty("queue", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return DrainVerdict.Unknown("vfs/queue did not return a queue list.");
        }
        int files = 0;
        long bytes = 0;
        var uploads = new List<UploadQueueItem>();
        var ids = new HashSet<ulong>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !Integer(item, "size", out long size)
                || size > long.MaxValue - bytes
                || !Integer(item, "tries", out long tries)
                || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt64(out ulong uploadId)
                || !ids.Add(uploadId)
                || !item.TryGetProperty("uploading", out var uploading) || uploading.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return DrainVerdict.Unknown("vfs/queue contains an invalid size, ID or upload retry status.");
            }
            files++;
            bytes += size;
            uploads.Add(new(uploadId, tries, uploading.GetBoolean()));
        }
        var reasons = new List<string>();
        // These counters describe cleanup of clean cached files, not failed uploads.
        bool cacheTrouble = errored > 0 || outOfSpace.GetBoolean();
        if (errored > 0) reasons.Add($"SSD cache cleanup is retrying {errored} clean cached file(s); check free space on the cache disk.");
        if (outOfSpace.GetBoolean()) reasons.Add("The SSD cache is freeing space; keep this PC on and check free space on the cache disk.");
        int retries = uploads.Count(u => !u.Uploading && u.Tries > 0);
        if (retries > 0) reasons.Add($"{retries} clip(s) failed to upload or were canceled and are being retried; check that primary storage accepts writes and has free space.");
        if (inProgress > 0 || queued > 0 || files > 0 || cacheTrouble)
        {
            if (inProgress > 0 || queued > 0 || files > 0)
                reasons.Add($"{Math.Max(files, inProgress + queued)} upload(s) remaining ({bytes / (1024.0 * 1024.0):0.0} MiB queued).");
            return new DrainVerdict(DrainState.Pending, reasons, files, bytes) { Uploads = uploads, CacheSpaceTrouble = cacheTrouble };
        }
        return new DrainVerdict(DrainState.Drained, [], 0, 0);
    }

    private static bool Integer(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value) && value >= 0;
    }

    /// <summary>rclone may report a local remote with forward slashes or a \\?\UNC\ prefix.</summary>
    internal static bool SameRemote(string reported, string expected) =>
        string.Equals(NormalizeRemote(reported), NormalizeRemote(expected), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRemote(string value)
    {
        string text = value.Replace('/', '\\').TrimEnd('\\');
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) text = @"\\" + text[8..];
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text[4..];
        return text;
    }
}

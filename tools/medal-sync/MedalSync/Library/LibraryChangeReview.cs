using System.Text.Json;

namespace MedalSync.Library;

/// <summary>An exact, stopped-library review; approval never carries into another session or snapshot.</summary>
internal sealed record LibraryChangeReview(string SessionId, long Generation, LibrarySnapshot Before,
    LibrarySnapshot After, IReadOnlyList<MediaChange> Changes)
{
    public IReadOnlyList<string> Pages()
    {
        var table = Before.Table("contents");
        int id = table.ColumnIndex("local_content_id");
        int metadata = table.ColumnIndex("metadata");
        var rows = table.Rows.ToDictionary(r => TableSnapshot.Text(r[id])!, StringComparer.Ordinal);
        var entries = Changes.GroupBy(c => c.ContentId, StringComparer.Ordinal).Select(group =>
        {
            var row = rows[group.Key];
            string? title = null;
            if (row[metadata] is JsonbCell json)
            {
                using var document = JsonDocument.Parse(json.Json);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("title", out var value) && value.ValueKind == JsonValueKind.String)
                    title = value.GetString();
                if (string.IsNullOrWhiteSpace(title) && document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("remoteContent", out var remote) && remote.ValueKind == JsonValueKind.Object
                    && remote.TryGetProperty("contentTitle", out var remoteTitle) && remoteTitle.ValueKind == JsonValueKind.String)
                    title = remoteTitle.GetString();
            }
            string? path = AssetInventory.PathColumns.Select(c => TableSnapshot.Text(row[table.ColumnIndex(c)]))
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            string? filename = path?.Replace('\\', '/').Split('/').Last();
            string label = string.IsNullOrWhiteSpace(title) ? filename ?? "Clip without a local file"
                : filename is null ? title : $"{title} ({filename})";
            label = new string(label.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
            if (label.Length > 180) label = label[..180] + "...";
            return $"{label}\n{group.Key}\n" + (group.Any(c => c.Kind == MediaChangeKind.RemovedWithoutRecord)
                ? "Removed from the library" : "File reference removed");
        }).ToArray();
        var pages = entries.Chunk(5).ToArray();
        return pages.Select((page, index) =>
            $"Did you intentionally remove ALL entries or file references below? ({index + 1}/{pages.Length})\n\n"
            + string.Join("\n\n", page)
            + "\n\nYes: keep these removals and continue syncing after all pages are confirmed."
            + "\nNo: leave the session unchanged for recovery."
            + "\n\nFiles still on the server will not be deleted by this confirmation.").ToArray();
    }
}

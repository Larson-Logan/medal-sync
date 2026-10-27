using System.Text.Json;
using System.Text.Json.Nodes;
using MedalSync.Util;

namespace MedalSync.Library;

internal sealed record IdentityMapping(string IncomingId, string SharedId);
internal sealed record EnrollmentFile(string Source, string CapturePath);
internal sealed record ReconciliationResult(LibrarySnapshot Snapshot, IReadOnlyList<IdentityMapping> Identities,
    IReadOnlyList<EnrollmentFile> Files, IReadOnlyList<string> Conflicts, int Added, int Matched);

/// <summary>
/// A conservative union for initial PC enrollment. The shared IDs stay stable;
/// missing rows never imply deletion. Source snapshots and assets are retained.
/// </summary>
internal static class LibraryReconciliation
{
    // These describe the particular local encoding, or cached cloud observations,
    // rather than user edits. All other competing populated values need review.
    private static readonly HashSet<string> EncodingFields = new(StringComparer.Ordinal)
    {
        "recorder", "formatName", "audioStreams", "bitRate", "gameState", "codecName", "height", "width",
        "colorSpace", "frameRate", "clipDuration", "thumbnailSize", "thumbnailInode", "contentSize", "contentInode",
        "uuid", "migratedAt", "uploadedAt", "thumbnailUploadedAt", "keyboardOverlayFileKey", "keyboardOverlayFilePath",
    };

    public static ReconciliationResult Plan(LibrarySnapshot shared, LibrarySnapshot incoming, WindowsPath source, WindowsPath destination)
    {
        if (shared.AccountId != incoming.AccountId || shared.SchemaFingerprint != incoming.SchemaFingerprint
            || shared.SchemaVersion != incoming.SchemaVersion || !MedalSchema.SupportedFingerprints.Contains(shared.SchemaFingerprint))
            throw new IncompatibleLibraryException("Both enrollment libraries must have the same supported account and schema.");
        if (source.IsWithin(destination) || destination.IsWithin(source))
            throw new UnsafeOperationException("Enrollment source and destination must not overlap.");
        foreach (string name in MedalSchema.LibraryTables)
            if (!shared.Table(name).Columns.SequenceEqual(incoming.Table(name).Columns))
                throw new IncompatibleLibraryException($"Enrollment {name} columns differ.");

        var conflicts = new List<string>();
        // A tombstone can name another PC's differently generated local ID inside
        // its JSONB record. Do not guess at delete-versus-keep during enrollment.
        if (shared.RowCount("deleted_contents") != 0 || incoming.RowCount("deleted_contents") != 0)
            conflicts.Add("Deletion records need explicit reconciliation before enrollment; neither side was changed.");
        var table = shared.Table("contents");
        int id = table.ColumnIndex("local_content_id"), remote = table.ColumnIndex("remote_content_id");
        int parent = table.ColumnIndex("parent_id"), metadata = table.ColumnIndex("metadata");
        var byId = table.Rows.ToDictionary(r => TableSnapshot.Text(r[id])!, StringComparer.Ordinal);
        var byRemote = new Dictionary<string, IReadOnlyList<Cell?>>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
            if (TableSnapshot.Text(row[remote]) is { Length: > 0 } key && !byRemote.TryAdd(key, row))
                throw new InvalidDataException("Shared library contains duplicate remote identities.");
        var incomingRemote = new HashSet<string>(StringComparer.Ordinal);
        var mappings = new List<IdentityMapping>();
        foreach (var row in incoming.Table("contents").Rows)
        {
            string localId = TableSnapshot.Text(row[id])!;
            string? remoteId = TableSnapshot.Text(row[remote]);
            if (remoteId is { Length: > 0 } && !incomingRemote.Add(remoteId))
                throw new InvalidDataException("Incoming library contains duplicate remote identities.");
            byId.TryGetValue(localId, out var sameId);
            var sameRemote = remoteId is not null ? byRemote.GetValueOrDefault(remoteId) : null;
            if (sameId is not null && sameRemote is not null && sameId != sameRemote)
                conflicts.Add($"{localId}: local and remote identities refer to different shared entries.");
            var match = sameId ?? sameRemote;
            if (sameId is not null && TableSnapshot.Text(sameId[remote]) is { Length: > 0 } oldRemote
                && remoteId is { Length: > 0 } && oldRemote != remoteId)
                conflicts.Add($"{localId}: the same local identity has two different remote identities.");
            mappings.Add(new(localId, match is null ? localId : TableSnapshot.Text(match[id])!));
        }
        var map = mappings.ToDictionary(m => m.IncomingId, m => m.SharedId, StringComparer.Ordinal);
        foreach (var aliases in mappings.GroupBy(m => m.SharedId, StringComparer.Ordinal).Where(g => g.Count() > 1))
            conflicts.Add($"{aliases.Key}: multiple incoming entries map to one shared identity; review them before merging.");
        var files = new Dictionary<string, EnrollmentFile>(StringComparer.OrdinalIgnoreCase);
        var rows = byId.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        int added = 0, matched = 0;
        foreach (var original in incoming.Table("contents").Rows)
        {
            string localId = TableSnapshot.Text(original[id])!;
            var row = original.ToArray();
            row[id] = new TextCell(map[localId]);
            if (TableSnapshot.Text(row[parent]) is { Length: > 0 } parentId)
            {
                if (map.TryGetValue(parentId, out string? mapped)) row[parent] = new TextCell(mapped);
                else if (!rows.ContainsKey(parentId)) conflicts.Add($"{localId}: parent {parentId} is not in either library.");
            }
            foreach (string column in AssetInventory.PathColumns)
            {
                int index = table.ColumnIndex(column);
                if (TableSnapshot.Text(row[index]) is { Length: > 0 } path)
                    row[index] = new TextCell(Relocate(path, localId));
            }
            if (row[metadata] is not JsonbCell json) throw new InvalidDataException("Enrollment metadata must be JSONB.");
            var node = JsonNode.Parse(json.Json) as JsonObject ?? throw new InvalidDataException("Enrollment metadata must be an object.");
            if (node["keyboardOverlayFilePath"] is JsonValue overlay && overlay.TryGetValue<string>(out string? overlayPath)
                && !string.IsNullOrEmpty(overlayPath)) node["keyboardOverlayFilePath"] = Relocate(overlayPath, localId);
            row[metadata] = new JsonbCell(node.ToJsonString());
            if (!rows.TryGetValue(map[localId], out var existing))
            {
                rows.Add(map[localId], row);
                added++;
                continue;
            }
            matched++;
            bool localMedia = HasMedia(row, table), sharedMedia = HasMedia(existing, table);
            var merged = (localMedia && !sharedMedia ? row : existing).ToArray();
            foreach (string column in new[] { "category_id", "parent_id", "remote_content_id" }.Concat(AssetInventory.PathColumns))
            {
                int index = table.ColumnIndex(column);
                string? a = TableSnapshot.Text(existing[index]), b = TableSnapshot.Text(row[index]);
                if (string.IsNullOrEmpty(a)) { if (!string.IsNullOrEmpty(b)) merged[index] = row[index]; }
                else if (string.IsNullOrEmpty(b)) merged[index] = existing[index];
                else if (a != b && column is not ("thumbnail_path" or "film_reel_path"))
                    conflicts.Add($"{map[localId]}: both libraries have different {column}; review both originals.");
            }
            // Preserve the shared ID even when the incoming row supplies its media.
            merged[id] = existing[id];
            merged[metadata] = MergeMetadata((JsonbCell)existing[metadata]!, (JsonbCell)row[metadata]!,
                localMedia && !sharedMedia, map[localId], conflicts);
            rows[map[localId]] = merged;
        }
        var snapshot = LibrarySnapshot.Create(shared.AccountId, shared.SchemaVersion, shared.SchemaFingerprint,
            shared.Tables.Select(t => t.Name == "contents" ? t with { Rows = rows.Values.ToArray() } : t));
        // Unsupported embedded local paths must not silently become dangling
        // device-specific references on the other PC.
        foreach (var asset in AssetInventory.Collect(snapshot).Where(a => a.Role == AssetRole.Unrecognized))
            conflicts.Add($"{asset.ContentId}: unsupported embedded asset {asset.Field}.");
        return new(snapshot, mappings, files.Values.OrderBy(f => f.Source, StringComparer.OrdinalIgnoreCase).ToArray(),
            conflicts.Distinct(StringComparer.Ordinal).ToArray(), added, matched);

        string Relocate(string text, string contentId)
        {
            if (!WindowsPath.TryParse(text, out var path, out _) || !path.IsWithin(source) || path.Equals(source))
            {
                conflicts.Add($"{contentId}: asset outside the selected source root: {text}.");
                return text;
            }
            string target = path.Rebase(source, destination).Display;
            files.TryAdd(path.Key, new(path.Display, target));
            return target;
        }
    }

    private static bool HasMedia(IReadOnlyList<Cell?> row, TableSnapshot table) =>
        new[] { "video_path", "image_path" }.Any(c => TableSnapshot.Text(row[table.ColumnIndex(c)]) is { Length: > 0 });

    private static JsonbCell MergeMetadata(JsonbCell shared, JsonbCell incoming, bool preferIncoming, string id, List<string> conflicts)
    {
        var a = JsonNode.Parse(shared.Json) as JsonObject ?? throw new InvalidDataException("Shared metadata must be an object.");
        var b = JsonNode.Parse(incoming.Json) as JsonObject ?? throw new InvalidDataException("Incoming metadata must be an object.");
        var merged = (JsonObject)(preferIncoming ? b : a).DeepClone();
        foreach (string key in a.Select(p => p.Key).Concat(b.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
        {
            JsonNode? left = a[key], right = b[key];
            if (key is "remoteContent" or "remoteSyncedAt") { merged[key] = (left ?? right)?.DeepClone(); continue; }
            if (Empty(left)) { merged[key] = right?.DeepClone(); continue; }
            if (Empty(right)) { merged[key] = left?.DeepClone(); continue; }
            if (JsonNode.DeepEquals(left, right) || EncodingFields.Contains(key)) continue;
            // Avatar/cache changes do not change who was tagged. Keep the shared
            // representation only when every tagged identity is identical.
            if (key is "userTags" or "playerTags" && SameTaggedUsers(left!, right!))
            { merged[key] = left!.DeepClone(); continue; }
            conflicts.Add($"{id}: competing metadata '{key}' needs review.");
        }
        return new JsonbCell(merged.ToJsonString());
    }

    private static bool Empty(JsonNode? node) => node is null || node is JsonArray { Count: 0 }
        || node is JsonObject { Count: 0 } || node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrEmpty(text);

    private static bool SameTaggedUsers(JsonNode left, JsonNode right)
    {
        if (left is not JsonArray a || right is not JsonArray b || a.Count != b.Count) return false;
        // Only the cached avatar may differ; names and every other field remain
        // meaningful and are compared exactly.
        static JsonNode Strip(JsonNode? item)
        {
            if (item is not JsonObject obj || obj["userId"] is null) return item?.DeepClone() ?? new JsonObject();
            var copy = (JsonObject)obj.DeepClone(); copy.Remove("thumbnail"); return copy;
        }
        return a.Zip(b).All(pair => JsonNode.DeepEquals(Strip(pair.First), Strip(pair.Second)));
    }
}

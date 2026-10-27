using System.Text.Json.Nodes;

namespace MedalSync.Library;

internal enum MediaChangeKind
{
    /// <summary>A path column went from a value to null: Medal's missing-media handling.</summary>
    PathCleared,

    /// <summary>A path column now names a different file.</summary>
    PathChanged,

    /// <summary>The entry is gone and no deletion was recorded.</summary>
    RemovedWithoutRecord,

    /// <summary>The entry is gone and <c>deleted_contents</c> records the deletion.</summary>
    RemovedWithRecord,
}

internal sealed record MediaChange(string ContentId, MediaChangeKind Kind, string? Field, string? Before, string? After);

internal sealed record MediaAudit(IReadOnlyList<MediaChange> Changes)
{
    /// <summary>Changes Medal makes when it cannot see a file, which an outage can cause.</summary>
    public IEnumerable<MediaChange> Suspect =>
        Changes.Where(c => c.Kind is MediaChangeKind.PathCleared or MediaChangeKind.RemovedWithoutRecord);
}

/// <summary>
/// Compares a library checkpoint taken before a session with the library after
/// it. Medal 2643.583.1 clears an entry's paths, or deletes never-uploaded
/// entries, when their files are not visible while the drive root still is.
/// A storage outage can therefore remove library entries whose files are fine.
/// </summary>
internal static class MissingMediaAudit
{
    /// <summary>Metadata Medal drops along with a file's path.</summary>
    private static readonly string[] FileIdentityKeys = ["contentSize", "contentInode", "thumbnailSize", "thumbnailInode"];

    public static MediaAudit Compare(LibrarySnapshot checkpoint, LibrarySnapshot current)
    {
        var before = Rows(checkpoint);
        var after = Rows(current);
        var deleted = DeletedIds(current);
        var contents = checkpoint.Table("contents");
        var changes = new List<MediaChange>();
        foreach (var (id, row) in before)
        {
            if (!after.TryGetValue(id, out var now))
            {
                changes.Add(new MediaChange(id, deleted.Contains(id) ? MediaChangeKind.RemovedWithRecord : MediaChangeKind.RemovedWithoutRecord, null, null, null));
                continue;
            }
            foreach (string column in AssetInventory.PathColumns)
            {
                int index = contents.ColumnIndex(column);
                string? was = TableSnapshot.Text(row[index]);
                string? @is = TableSnapshot.Text(now[index]);
                if (was is null || was == @is) continue;
                changes.Add(new MediaChange(id, @is is null ? MediaChangeKind.PathCleared : MediaChangeKind.PathChanged, column, was, @is));
            }
        }
        return new MediaAudit(changes);
    }

    /// <summary>
    /// Builds a repaired copy of <paramref name="current"/>: cleared paths whose
    /// files exist again are put back, and silently removed entries whose media
    /// exists are reinserted. Nothing else changes. Apply it with the restorer
    /// only after the user has reviewed <paramref name="repaired"/>.
    /// </summary>
    public static LibrarySnapshot ProposeRepair(LibrarySnapshot checkpoint, LibrarySnapshot current, Func<string, bool> fileExists,
        out IReadOnlyList<MediaChange> repaired)
    {
        var audit = Compare(checkpoint, current);
        var table = current.Table("contents");
        int metadata = table.ColumnIndex("metadata");
        var before = Rows(checkpoint);
        var rows = Rows(current).ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        var applied = new List<MediaChange>();
        foreach (var change in audit.Suspect)
        {
            var original = before[change.ContentId];
            if (change.Kind == MediaChangeKind.PathCleared && fileExists(change.Before!))
            {
                var row = rows[change.ContentId];
                row[table.ColumnIndex(change.Field!)] = new TextCell(change.Before!);
                row[metadata] = RestoreIdentity(original[metadata], row[metadata]);
                applied.Add(change);
            }
            else if (change.Kind == MediaChangeKind.RemovedWithoutRecord && HasExistingMedia(original, table, fileExists))
            {
                rows[change.ContentId] = original.ToArray();
                applied.Add(change);
            }
        }
        repaired = applied;
        var tables = current.Tables.Select(t => t.Name == "contents"
            ? t with { Rows = rows.Values.Select(r => (IReadOnlyList<Cell?>)r).ToArray() }
            : t);
        return LibrarySnapshot.Create(current.AccountId, current.SchemaVersion, current.SchemaFingerprint, tables);
    }

    private static bool HasExistingMedia(IReadOnlyList<Cell?> row, TableSnapshot table, Func<string, bool> fileExists) =>
        new[] { "video_path", "image_path" }
            .Select(c => TableSnapshot.Text(row[table.ColumnIndex(c)]))
            .Any(p => p is not null && fileExists(p));

    /// <summary>Copies file-identity keys the checkpoint had and the current metadata lost.</summary>
    private static Cell? RestoreIdentity(Cell? original, Cell? current)
    {
        if (original is not JsonbCell before || current is not JsonbCell after) return current;
        var source = JsonNode.Parse(before.Json) as JsonObject;
        var target = JsonNode.Parse(after.Json) as JsonObject;
        if (source is null || target is null) return current;
        bool changed = false;
        foreach (string key in FileIdentityKeys)
        {
            if (!target.ContainsKey(key) && source[key] is JsonNode value)
            {
                target[key] = value.DeepClone();
                changed = true;
            }
        }
        return changed ? new JsonbCell(SqliteJson.Canonical(target.ToJsonString())) : current;
    }

    private static Dictionary<string, IReadOnlyList<Cell?>> Rows(LibrarySnapshot snapshot)
    {
        var table = snapshot.Table("contents");
        int id = table.ColumnIndex("local_content_id");
        return table.Rows.ToDictionary(r => TableSnapshot.Text(r[id])!, StringComparer.Ordinal);
    }

    private static HashSet<string> DeletedIds(LibrarySnapshot snapshot)
    {
        var table = snapshot.Table("deleted_contents");
        int id = table.ColumnIndex("local_content_id");
        return table.Rows.Select(r => TableSnapshot.Text(r[id])!).ToHashSet(StringComparer.Ordinal);
    }
}

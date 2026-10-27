using MedalSync.Util;

namespace MedalSync.Library;

/// <summary>One library path that moves from an old folder to the capture folder.</summary>
internal sealed record Relocation(string ContentId, string Column, string Original, WindowsPath From, WindowsPath To);

/// <summary>
/// Rewrites library file paths from a folder outside the capture tree (files
/// Medal's own storage move left behind) to the same relative path inside it.
/// Only the path columns change; metadata and every other cell stay as they are.
/// </summary>
internal static class ReferenceRelocation
{
    public static IReadOnlyList<Relocation> Plan(LibrarySnapshot snapshot, WindowsPath fromRoot, WindowsPath toRoot)
    {
        if (fromRoot.IsWithin(toRoot) || toRoot.IsWithin(fromRoot))
        {
            throw new UnsafeOperationException($"{fromRoot.Display} and {toRoot.Display} must not contain each other.");
        }
        var contents = snapshot.Table("contents");
        int id = contents.ColumnIndex("local_content_id");
        var columns = AssetInventory.PathColumns.Select(c => (Name: c, Index: contents.ColumnIndex(c))).ToArray();
        var moves = new List<Relocation>();
        foreach (var row in contents.Rows)
        {
            foreach (var (name, index) in columns)
            {
                if (TableSnapshot.Text(row[index]) is not { Length: > 0 } value) continue;
                if (!WindowsPath.TryParse(value, out WindowsPath? path, out _) || !path.IsWithin(fromRoot) || path.Equals(fromRoot)) continue;
                moves.Add(new Relocation(TableSnapshot.Text(row[id])!, name, value, path, path.Rebase(fromRoot, toRoot)));
            }
        }
        return moves;
    }

    public static LibrarySnapshot Apply(LibrarySnapshot snapshot, IReadOnlyList<Relocation> moves)
    {
        var contents = snapshot.Table("contents");
        int id = contents.ColumnIndex("local_content_id");
        var byRow = moves.ToLookup(m => m.ContentId, StringComparer.Ordinal);
        int applied = 0;
        var rows = contents.Rows.Select(row =>
        {
            var changes = byRow[TableSnapshot.Text(row[id])!].ToList();
            if (changes.Count == 0) return row;
            var copy = row.ToArray();
            foreach (var move in changes)
            {
                int index = contents.ColumnIndex(move.Column);
                // A path that changed since the plan was made is left alone and fails the count check below.
                if (TableSnapshot.Text(copy[index]) != move.Original) continue;
                copy[index] = new TextCell(move.To.Display);
                applied++;
            }
            return (IReadOnlyList<Cell?>)copy;
        }).ToArray();
        if (applied != moves.Count)
        {
            throw new UnsafeOperationException($"Only {applied} of {moves.Count} planned path change(s) matched the library; nothing was changed.");
        }
        var tables = snapshot.Tables.Select(t => t.Name == "contents" ? t with { Rows = rows } : t);
        return LibrarySnapshot.Create(snapshot.AccountId, snapshot.SchemaVersion, snapshot.SchemaFingerprint, tables);
    }
}

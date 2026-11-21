using System.Text.Json;
using System.Text.Json.Nodes;
using MedalSync.Util;

namespace MedalSync.Library;

/// <summary>One library path that moves from an old folder to the capture folder.</summary>
internal sealed record Relocation(string ContentId, string Field, string Original, WindowsPath From, WindowsPath To, AssetRole Role);

/// <summary>
/// Rewrites library file paths from a folder outside the capture tree (files
/// Medal's own storage move left behind) to the same relative path inside it.
/// Only Medal's path columns and its keyboard overlay path are rewritten.
/// </summary>
internal static class ReferenceRelocation
{
    private const string OverlayProperty = "keyboardOverlayFilePath";
    private const string OverlayField = "metadata:$." + OverlayProperty;
    private static readonly JsonDocumentOptions MetadataOptions = new() { MaxDepth = 256 };

    public static IReadOnlyList<Relocation> Plan(LibrarySnapshot snapshot, WindowsPath fromRoot, WindowsPath toRoot)
    {
        if (fromRoot.IsWithin(toRoot) || toRoot.IsWithin(fromRoot))
        {
            throw new UnsafeOperationException($"{fromRoot.Display} and {toRoot.Display} must not contain each other.");
        }
        var moves = new List<Relocation>();
        foreach (var reference in AssetInventory.Collect(snapshot))
        {
            if (reference.Role != AssetRole.Required && reference.Field != OverlayField) continue;
            if (!WindowsPath.TryParse(reference.Path, out WindowsPath? path, out _) || !path.IsWithin(fromRoot) || path.Equals(fromRoot)) continue;
            moves.Add(new Relocation(reference.ContentId, reference.Field, reference.Path, path, path.Rebase(fromRoot, toRoot), reference.Role));
        }
        return moves;
    }

    public static LibrarySnapshot Apply(LibrarySnapshot snapshot, IReadOnlyList<Relocation> moves, IReadOnlySet<string>? missingOptional = null)
    {
        var contents = snapshot.Table("contents");
        int id = contents.ColumnIndex("local_content_id");
        int metadata = contents.ColumnIndex("metadata");
        var byRow = moves.ToLookup(m => m.ContentId, StringComparer.Ordinal);
        int applied = 0;
        var rows = contents.Rows.Select(row =>
        {
            var changes = byRow[TableSnapshot.Text(row[id])!].ToList();
            if (changes.Count == 0) return row;
            var copy = row.ToArray();
            foreach (var move in changes)
            {
                if (move.Field == OverlayField)
                {
                    if (copy[metadata] is not JsonbCell json
                        || JsonNode.Parse(json.Json, documentOptions: MetadataOptions) is not JsonObject node
                        || node[OverlayProperty] is not JsonValue overlay || !overlay.TryGetValue<string>(out string? value)
                        || value != move.Original) continue;
                    // A missing source cannot verify any same-named file at the destination.
                    if (missingOptional?.Contains(move.From.Key) == true) node.Remove(OverlayProperty);
                    else node[OverlayProperty] = move.To.Display;
                    copy[metadata] = new JsonbCell(node.ToJsonString());
                    applied++;
                    continue;
                }
                int index = contents.ColumnIndex(move.Field);
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

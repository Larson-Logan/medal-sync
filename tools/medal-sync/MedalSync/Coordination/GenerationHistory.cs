using System.Globalization;
using System.Text;
using System.Text.Json;
using MedalSync.Library;
using MedalSync.Util;

namespace MedalSync.Coordination;

/// <summary>One generation folder as <c>history list</c> shows it.</summary>
/// <param name="Committed">
/// Part of the committed head's ancestry. False for folders left by an abandoned or unfinished publish; null below a break
/// in the ancestry, where it cannot be known.
/// </param>
/// <param name="Problem">Why this generation could not be read; its other fields are then null.</param>
internal sealed record HistoryEntry(long Generation, long? Parent, bool? Committed, bool Head, DateTimeOffset? CreatedAt, string? Device,
    string? ToolVersion, string? MedalApp, string? MedalRecorder, IReadOnlyDictionary<string, int>? RowCounts, string? Problem);

/// <summary>
/// A page of generations, newest first. <see cref="Complete"/> is false when the committed ancestry could not be
/// followed back to generation 0; history before that point is reported as unknown, never invented.
/// </summary>
internal sealed record HistoryList(int Format, long Head, DateTimeOffset ObservedAt, bool Complete, string? Problem,
    IReadOnlyList<HistoryEntry> Entries, long? NextBefore, IReadOnlyList<PrunedRange>? Pruned = null)
{
    public const int CurrentFormat = 1;
}

/// <summary>Consecutive generation numbers that retention removed on purpose; not damage.</summary>
internal sealed record PrunedRange(long From, long To, DateTimeOffset PrunedAt);

internal sealed record HistoryCounts(
    int Added,
    int Removed,
    int RemovedWithDeletionRecord,
    int RemovedWithoutRecord,
    int Changed,
    int DeletionRecordsAdded,
    int DeletionRecordsRemoved,
    int CounterChanges,
    IReadOnlyDictionary<string, int> PathChanges,
    IReadOnlyDictionary<string, int> MetadataFields,
    IReadOnlyDictionary<string, int> OtherFields);

internal sealed record HistoryPathChange(string? Before, string? After);

/// <summary>One clip's change. Holds titles and paths, so it is only ever written to a file the user names.</summary>
internal sealed record HistoryItem(string ContentId, string Change, string? Title, IReadOnlyList<string> Fields,
    IReadOnlyDictionary<string, HistoryPathChange> Paths);

/// <param name="FromIsAncestor">Whether <see cref="From"/> is in <see cref="To"/>'s ancestry; null when that ancestry could not be read.</param>
internal sealed record HistoryDiff(int Format, long From, long To, long Head, DateTimeOffset ObservedAt, bool? FromIsAncestor, HistoryCounts Counts)
{
    public const int CurrentFormat = 1;
}

/// <summary>
/// Read-only views of the immutable generations: never takes the writer lock or ownership, never restores, prunes or
/// changes anything. The committed head is read once from the journal, and every report names the head it observed.
/// </summary>
internal static class GenerationHistory
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 1000;

    public const string Added = "added";
    public const string RemovedWithRecord = "removed-with-deletion-record";
    public const string RemovedWithoutRecord = "removed-without-record";
    public const string Changed = "changed";

    public static HistoryList List(GenerationStore generations, long head, int limit, long? before, DateTimeOffset now)
    {
        if (limit is < 1 or > MaxLimit) throw new ArgumentOutOfRangeException(nameof(limit));
        var (chain, complete, problem) = Ancestry(generations, head);
        var numbers = generations.Numbered().Concat(chain.Where(c => c.Value.Problem != PrunedProblem).Select(c => c.Key)).Distinct()
            .Where(n => before is null || n < before).OrderDescending().ToArray();
        long oldestKnown = chain.Keys.Min();
        var entries = numbers.Take(limit).Select(n => chain.TryGetValue(n, out var known)
            ? Entry(n, known.Manifest, known.Problem, committed: true, n == head)
            : Uncommitted(generations, n, complete || n > oldestKnown ? false : null)).ToArray();
        return new(HistoryList.CurrentFormat, head, now, complete, problem, entries,
            numbers.Length > limit ? entries[^1].Generation : null, PrunedRanges(generations));
    }

    private const string PrunedProblem = "pruned";

    private static IReadOnlyList<PrunedRange> PrunedRanges(GenerationStore generations)
    {
        var pruned = (generations.ReadRetention()?.Pruned ?? []).Where(p => !generations.Exists(p.Generation)).OrderBy(p => p.Generation).ToArray();
        var ranges = new List<PrunedRange>();
        foreach (var p in pruned)
        {
            if (ranges.Count > 0 && ranges[^1].To + 1 == p.Generation)
                ranges[^1] = ranges[^1] with { To = p.Generation, PrunedAt = p.PrunedAt > ranges[^1].PrunedAt ? p.PrunedAt : ranges[^1].PrunedAt };
            else ranges.Add(new(p.Generation, p.Generation, p.PrunedAt));
        }
        return ranges;
    }

    /// <summary>
    /// Compares two verified generations of the same schema. Damaged generations and different schemas are refused
    /// with <see cref="InvalidDataException"/> or <see cref="IncompatibleLibraryException"/>.
    /// </summary>
    public static (HistoryDiff Diff, IReadOnlyList<HistoryItem> Items) Diff(GenerationStore generations, long head, long from, long to, DateTimeOffset now)
    {
        var before = ReadForDiff(generations, from);
        var after = ReadForDiff(generations, to);
        if (before.Manifest.SchemaFingerprint != after.Manifest.SchemaFingerprint || before.Snapshot.SchemaVersion != after.Snapshot.SchemaVersion)
            throw new IncompatibleLibraryException($"Generations {from} and {to} use different Medal schemas, so their rows cannot be compared field by field. Nothing was changed.");
        var (counts, items) = Compare(before.Snapshot, after.Snapshot);
        return (new(HistoryDiff.CurrentFormat, from, to, head, now, IsAncestor(generations, from, to), counts), items);
    }

    private static GenerationContent ReadForDiff(GenerationStore generations, long generation)
    {
        if (generations.Pruned(generation) is { } pruned)
            throw new InvalidDataException($"Generation {generation} was removed by retention on {pruned.PrunedAt:yyyy-MM-dd}, so it cannot be compared. Nothing was changed.");
        try { return generations.Read(generation); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or KeyNotFoundException)
        {
            throw new InvalidDataException($"Generation {generation} cannot be compared: {ex.Message} Nothing was changed.", ex);
        }
    }

    /// <summary>The committed head and its parents, following each manifest's parent number down to generation 0.</summary>
    private static (Dictionary<long, (GenerationManifest? Manifest, string? Problem)> Chain, bool Complete, string? Problem) Ancestry(
        GenerationStore generations, long head)
    {
        var chain = new Dictionary<long, (GenerationManifest?, string?)>();
        GenerationRetention? retention;
        try { retention = generations.ReadRetention(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { retention = null; }
        long? current = head;
        while (current is { } number)
        {
            if (!generations.Exists(number) && retention?.Find(number) is not null)
            {
                chain[number] = (null, PrunedProblem);
                var (known, parent) = retention.ParentOf(number);
                if (!known) return (chain, false, $"History before generation {number} was removed by retention and is not recorded; older history is unknown.");
                current = parent;
                continue;
            }
            if (!generations.Exists(number))
            {
                chain[number] = (null, "missing");
                return (chain, false, $"Generation {number} in the committed history is missing; older history is unknown.");
            }
            GenerationManifest manifest;
            try { manifest = generations.ReadManifest(number); }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                chain[number] = (null, "damaged");
                return (chain, false, $"Generation {number} in the committed history cannot be read; older history is unknown.");
            }
            chain[number] = (manifest, null);
            if (manifest.ParentGeneration is { } parent && (parent < 0 || parent >= number))
                return (chain, false, $"Generation {number} names parent {parent}, which is not older; older history is unknown.");
            current = manifest.ParentGeneration;
        }
        return (chain, true, null);
    }

    private static bool? IsAncestor(GenerationStore generations, long ancestor, long generation)
    {
        try
        {
            foreach (var (number, _) in generations.Chain(generation))
            {
                if (number == ancestor) return true;
                if (number < ancestor) return false;
            }
            return false;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return null; }
    }

    private static HistoryEntry Uncommitted(GenerationStore generations, long number, bool? committed)
    {
        try { return Entry(number, generations.ReadManifest(number), null, committed, head: false); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return Entry(number, null, "damaged", committed, head: false); }
    }

    private static HistoryEntry Entry(long number, GenerationManifest? manifest, string? problem, bool? committed, bool head) =>
        manifest is null
            ? new(number, null, committed, head, null, null, null, null, null, null, problem ?? "damaged")
            : new(number, manifest.ParentGeneration, committed, head, manifest.CreatedAt, manifest.Device.DisplayName, manifest.ToolVersion,
                manifest.Medal?.App, manifest.Medal?.Recorder, manifest.RowCounts, null);

    internal static (HistoryCounts Counts, IReadOnlyList<HistoryItem> Items) Compare(LibrarySnapshot before, LibrarySnapshot after)
    {
        var oldContents = before.Table("contents");
        var newContents = after.Table("contents");
        var oldRows = Rows(oldContents);
        var newRows = Rows(newContents);
        var oldDeleted = Rows(before.Table("deleted_contents")).Keys.ToHashSet(StringComparer.Ordinal);
        var newDeleted = Rows(after.Table("deleted_contents")).Keys.ToHashSet(StringComparer.Ordinal);
        var paths = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var metadata = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var other = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var items = new List<HistoryItem>();
        int added = 0, withRecord = 0, withoutRecord = 0, changed = 0;
        string metadataColumn = MedalSchema.JsonColumn("contents")!;
        string key = MedalSchema.NaturalKey("contents");

        foreach (var (id, row) in newRows.Where(r => !oldRows.ContainsKey(r.Key)))
        {
            added++;
            items.Add(new(id, Added, Title(newContents, row), [], PathsOf(newContents, row, removed: false)));
        }
        foreach (var (id, row) in oldRows.Where(r => !newRows.ContainsKey(r.Key)))
        {
            bool recorded = newDeleted.Contains(id);
            if (recorded) withRecord++; else withoutRecord++;
            items.Add(new(id, recorded ? RemovedWithRecord : RemovedWithoutRecord, Title(oldContents, row), [], PathsOf(oldContents, row, removed: true)));
        }
        foreach (var (id, newRow) in newRows)
        {
            if (!oldRows.TryGetValue(id, out var oldRow)) continue;
            var fields = new List<string>();
            var pathChanges = new SortedDictionary<string, HistoryPathChange>(StringComparer.Ordinal);
            foreach (string column in newContents.Columns)
            {
                if (column == key) continue;
                Cell? was = oldContents.Columns.Contains(column) ? oldRow[oldContents.ColumnIndex(column)] : null;
                Cell? now = newRow[newContents.ColumnIndex(column)];
                if (Equals(was, now)) continue;
                if (column == metadataColumn)
                {
                    foreach (string field in MetadataDifferences(was, now))
                    {
                        fields.Add("metadata." + field);
                        Increment(metadata, field);
                    }
                }
                else if (AssetInventory.PathColumns.Contains(column))
                {
                    fields.Add(column);
                    pathChanges[column] = new(TableSnapshot.Text(was), TableSnapshot.Text(now));
                    Increment(paths, column);
                }
                else
                {
                    fields.Add(column);
                    Increment(other, column);
                }
            }
            if (fields.Count == 0) continue;
            changed++;
            items.Add(new(id, Changed, Title(newContents, newRow), fields, pathChanges));
        }
        int counters = CounterChanges(before.Table("content_counts"), after.Table("content_counts"));
        var counts = new HistoryCounts(added, withRecord + withoutRecord, withRecord, withoutRecord, changed,
            newDeleted.Count(id => !oldDeleted.Contains(id)), oldDeleted.Count(id => !newDeleted.Contains(id)), counters,
            paths, metadata, other);
        return (counts, items.OrderBy(i => i.ContentId, StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, IReadOnlyList<Cell?>> Rows(TableSnapshot table)
    {
        int key = table.ColumnIndex(MedalSchema.NaturalKey(table.Name));
        return table.Rows.ToDictionary(r => TableSnapshot.Text(r[key])!, StringComparer.Ordinal);
    }

    private static int CounterChanges(TableSnapshot before, TableSnapshot after)
    {
        var old = Rows(before);
        var current = Rows(after);
        return current.Count(r => !old.TryGetValue(r.Key, out var was) || !was.SequenceEqual(r.Value))
            + old.Keys.Count(k => !current.ContainsKey(k));
    }

    /// <summary>Top-level metadata keys that were added, removed or changed. Unparseable metadata counts as one whole-value change.</summary>
    private static IEnumerable<string> MetadataDifferences(Cell? before, Cell? after)
    {
        var old = Fields(before);
        var current = Fields(after);
        if (old is null || current is null) return ["(whole value)"];
        return old.Keys.Union(current.Keys, StringComparer.Ordinal)
            .Where(k => !old.TryGetValue(k, out var a) || !current.TryGetValue(k, out var b) || a != b)
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, string>? Fields(Cell? cell)
    {
        if (cell is not JsonbCell json) return null;
        try
        {
            using var document = JsonDocument.Parse(json.Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) fields[property.Name] = property.Value.GetRawText();
            return fields;
        }
        catch (JsonException) { return null; }
    }

    private static string? Title(TableSnapshot table, IReadOnlyList<Cell?> row)
    {
        var fields = Fields(row[table.ColumnIndex(MedalSchema.JsonColumn("contents")!)]);
        if (fields is null || !fields.TryGetValue("title", out string? raw)) return null;
        try { return JsonSerializer.Deserialize<string>(raw); }
        catch (JsonException) { return null; }
    }

    /// <summary>The file paths of a clip that was added (after) or removed (before).</summary>
    private static Dictionary<string, HistoryPathChange> PathsOf(TableSnapshot table, IReadOnlyList<Cell?> row, bool removed) =>
        AssetInventory.PathColumns.Where(table.Columns.Contains)
            .Select(c => (Column: c, Path: TableSnapshot.Text(row[table.ColumnIndex(c)])))
            .Where(p => p.Path is not null)
            .ToDictionary(p => p.Column, p => removed ? new HistoryPathChange(p.Path, null) : new HistoryPathChange(null, p.Path), StringComparer.Ordinal);

    private static void Increment(IDictionary<string, int> counts, string key) => counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;

    public static string Render(HistoryList list)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Committed head: generation {list.Head} (read {list.ObservedAt:yyyy-MM-dd HH:mm} UTC).");
        foreach (var entry in list.Entries)
        {
            string mark = entry.Head ? "head" : entry.Committed switch { true => "", false => "uncommitted", null => "unknown" };
            text.Append(CultureInfo.InvariantCulture, $"{entry.Generation,6}  {mark,-11}  ");
            if (entry.Problem is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{entry.Problem}: this generation cannot be read.");
                continue;
            }
            string counts = entry.RowCounts is null ? "" : string.Join(", ", entry.RowCounts.OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Key} {c.Value:N0}")));
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{entry.CreatedAt:yyyy-MM-dd HH:mm} UTC  {entry.Device}  medal-sync {entry.ToolVersion}  Medal {entry.MedalApp ?? "unknown"}  "
                + $"parent {(entry.Parent is { } p ? p.ToString(CultureInfo.InvariantCulture) : "none")}  {counts}");
        }
        if (list.Entries.Any(e => e.Committed == false))
            text.AppendLine("Uncommitted generations were left by an abandoned or unfinished switch. They are not part of the shared history.");
        foreach (var range in list.Pruned ?? [])
            text.AppendLine(CultureInfo.InvariantCulture, range.From == range.To
                ? $"Generation {range.From} was removed by retention on {range.PrunedAt:yyyy-MM-dd}; this is not damage."
                : $"Generations {range.From}-{range.To} were removed by retention on {range.PrunedAt:yyyy-MM-dd}; this is not damage.");
        if (!list.Complete) text.AppendLine(CultureInfo.InvariantCulture, $"History is incomplete: {list.Problem} Generations marked unknown may or may not have been committed.");
        if (list.NextBefore is { } next) text.AppendLine(CultureInfo.InvariantCulture, $"More: history list --before {next}");
        return text.ToString().TrimEnd();
    }

    public static string Render(HistoryDiff diff)
    {
        var c = diff.Counts;
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Changes from generation {diff.From} to {diff.To} (committed head {diff.Head}, read {diff.ObservedAt:yyyy-MM-dd HH:mm} UTC).");
        text.AppendLine(diff.FromIsAncestor switch
        {
            true => $"Generation {diff.From} is an ancestor of {diff.To}.",
            false => $"Generation {diff.From} is not an ancestor of {diff.To}; these are separate branches or in reverse order.",
            null => $"Whether generation {diff.From} is an ancestor of {diff.To} is unknown: the history between them cannot be read.",
        });
        text.AppendLine(CultureInfo.InvariantCulture, $"Clips added: {c.Added:N0}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Clips removed: {c.Removed:N0} ({c.RemovedWithDeletionRecord:N0} with a deletion record, {c.RemovedWithoutRecord:N0} without)");
        text.AppendLine(CultureInfo.InvariantCulture, $"Clips changed: {c.Changed:N0}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Deletion records added: {c.DeletionRecordsAdded:N0}, removed: {c.DeletionRecordsRemoved:N0}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Library counters changed: {c.CounterChanges:N0}");
        AppendCounts(text, "File path changes", c.PathChanges);
        AppendCounts(text, "Metadata fields changed", c.MetadataFields);
        AppendCounts(text, "Other fields changed", c.OtherFields);
        return text.ToString().TrimEnd();
    }

    private static void AppendCounts(StringBuilder text, string label, IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0) return;
        text.AppendLine(CultureInfo.InvariantCulture, $"{label}: {string.Join(", ", counts.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Key} {c.Value:N0}")))}");
    }

    /// <summary>Writes the per-clip details to a new file; never replaces an existing one.</summary>
    public static void WriteDetails(string path, HistoryDiff diff, IReadOnlyList<HistoryItem> items) =>
        AtomicFile.CreateNew(path, Json.Serialize(new { diff.Format, diff.From, diff.To, diff.Head, diff.ObservedAt, items }));

    public const string PrivacyNotice = "The details file lists clip titles, IDs and file paths. Keep it on this PC; do not attach it to a public issue.";
}

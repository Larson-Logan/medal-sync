using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using LarsonServer.MedalSync.Util;

namespace LarsonServer.MedalSync.Library;

/// <summary>One stored value. A null cell is represented by a C# null.</summary>
internal abstract record Cell;

internal sealed record IntegerCell(long Value) : Cell;

internal sealed record TextCell(string Value) : Cell;

/// <summary>A JSONB value, carried as the exact text SQLite's <c>json()</c> renders.</summary>
internal sealed record JsonbCell(string Json) : Cell;

internal sealed record TableSnapshot(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<Cell?>> Rows)
{
    public int ColumnIndex(string column)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (Columns[i] == column) return i;
        }
        throw new KeyNotFoundException($"{Name} has no column '{column}'.");
    }

    public static string? Text(Cell? cell) => cell is TextCell text ? text.Value : null;
}

/// <summary>
/// The library tables of one Medal database. Rows are sorted by each table's
/// natural key, so equal libraries serialize to identical bytes and digests.
/// Device settings (<c>key_values</c>) are never part of a snapshot.
/// </summary>
internal sealed class LibrarySnapshot
{
    public const int FormatVersion = 1;

    private byte[]? _canonical;

    private LibrarySnapshot(string accountId, long schemaVersion, string schemaFingerprint, IReadOnlyList<TableSnapshot> tables)
    {
        AccountId = accountId;
        SchemaVersion = schemaVersion;
        SchemaFingerprint = schemaFingerprint;
        Tables = tables;
    }

    public string AccountId { get; }

    public long SchemaVersion { get; }

    public string SchemaFingerprint { get; }

    public IReadOnlyList<TableSnapshot> Tables { get; }

    public TableSnapshot Table(string name) =>
        Tables.FirstOrDefault(t => t.Name == name) ?? throw new KeyNotFoundException($"No table '{name}' in the snapshot.");

    public int RowCount(string table) => Table(table).Rows.Count;

    /// <summary>Validates and canonicalizes. Throws on duplicate or missing identities.</summary>
    public static LibrarySnapshot Create(string accountId, long schemaVersion, string schemaFingerprint, IEnumerable<TableSnapshot> tables)
    {
        if (string.IsNullOrWhiteSpace(accountId) || !accountId.All(char.IsAsciiDigit))
        {
            throw new InvalidDataException($"'{accountId}' is not a Medal account ID.");
        }
        var byName = tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var ordered = new List<TableSnapshot>();
        foreach (string name in MedalSchema.LibraryTables)
        {
            if (!byName.Remove(name, out TableSnapshot? table)) throw new InvalidDataException($"The snapshot has no '{name}' table.");
            ordered.Add(Canonicalize(table));
        }
        if (byName.Count > 0) throw new InvalidDataException($"Unexpected tables in the snapshot: {string.Join(", ", byName.Keys)}.");
        return new LibrarySnapshot(accountId, schemaVersion, schemaFingerprint, ordered);
    }

    private static TableSnapshot Canonicalize(TableSnapshot table)
    {
        if (table.Columns.Distinct(StringComparer.Ordinal).Count() != table.Columns.Count)
        {
            throw new InvalidDataException($"{table.Name} lists a column twice.");
        }
        int key = table.ColumnIndex(MedalSchema.NaturalKey(table.Name));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            if (row.Count != table.Columns.Count) throw new InvalidDataException($"A {table.Name} row has {row.Count} values for {table.Columns.Count} columns.");
            string id = TableSnapshot.Text(row[key]) ?? throw new InvalidDataException($"A {table.Name} row has no {table.Columns[key]}.");
            if (!seen.Add(id)) throw new InvalidDataException($"{table.Name} contains '{id}' twice.");
        }
        var rows = table.Rows
            .OrderBy(r => TableSnapshot.Text(r[key]), StringComparer.Ordinal)
            .Select(r => (IReadOnlyList<Cell?>)r.ToArray())
            .ToArray();
        return table with { Columns = table.Columns.ToArray(), Rows = rows };
    }

    /// <summary>Deterministic UTF-8 JSON. JSONB cells are embedded as their raw JSON text.</summary>
    public byte[] ToCanonicalJson() => (byte[])Canonical().Clone();

    public string Digest() => Hashing.Sha256Hex(Canonical());

    private byte[] Canonical()
    {
        if (_canonical is not null) return _canonical;
        var buffer = new ArrayBufferWriter<byte>();
        // Relaxed escaping only changes how characters are spelled; the output is
        // never embedded in HTML, and it is still deterministic.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", FormatVersion);
            writer.WriteString("accountId", AccountId);
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("schemaFingerprint", SchemaFingerprint);
            writer.WriteStartArray("tables");
            foreach (var table in Tables) WriteTable(writer, table);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        _canonical = buffer.WrittenSpan.ToArray();
        return _canonical;
    }

    private static void WriteTable(Utf8JsonWriter writer, TableSnapshot table)
    {
        writer.WriteStartObject();
        writer.WriteString("name", table.Name);
        writer.WriteStartArray("columns");
        foreach (string column in table.Columns) writer.WriteStringValue(column);
        writer.WriteEndArray();
        writer.WriteStartArray("rows");
        foreach (var row in table.Rows)
        {
            writer.WriteStartArray();
            foreach (var cell in row)
            {
                switch (cell)
                {
                    case null: writer.WriteNullValue(); break;
                    case IntegerCell i: writer.WriteNumberValue(i.Value); break;
                    case TextCell t: writer.WriteStringValue(t.Value); break;
                    case JsonbCell j:
                        writer.WriteStartObject();
                        writer.WritePropertyName("jsonb");
                        writer.WriteRawValue(j.Json, skipInputValidation: false);
                        writer.WriteEndObject();
                        break;
                    default: throw new InvalidOperationException($"Unknown cell type {cell.GetType().Name}.");
                }
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static LibrarySnapshot FromCanonicalJson(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 256 });
            var root = document.RootElement;
            int format = root.GetProperty("format").GetInt32();
            if (format != FormatVersion) throw new InvalidDataException($"Snapshot format {format} is not supported (expected {FormatVersion}).");
            var tables = root.GetProperty("tables").EnumerateArray().Select(ReadTable).ToList();
            return Create(root.GetProperty("accountId").GetString() ?? "", root.GetProperty("schemaVersion").GetInt64(),
                root.GetProperty("schemaFingerprint").GetString() ?? "", tables);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"The library snapshot is malformed: {ex.Message}", ex);
        }
    }

    private static TableSnapshot ReadTable(JsonElement element)
    {
        string name = element.GetProperty("name").GetString() ?? "";
        var columns = element.GetProperty("columns").EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
        var rows = element.GetProperty("rows").EnumerateArray()
            .Select(r => (IReadOnlyList<Cell?>)r.EnumerateArray().Select(ReadCell).ToArray())
            .ToArray();
        return new TableSnapshot(name, columns, rows);
    }

    private static Cell? ReadCell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Number when value.TryGetInt64(out long number) => new IntegerCell(number),
        JsonValueKind.String => new TextCell(value.GetString()!),
        JsonValueKind.Object when value.EnumerateObject().Count() == 1 && value.TryGetProperty("jsonb", out JsonElement json) =>
            new JsonbCell(json.GetRawText()),
        _ => throw new InvalidDataException($"Unsupported snapshot value: {value.GetRawText()}"),
    };
}

using System.Text;
using System.Text.RegularExpressions;
using LarsonServer.MedalSync.Util;
using Microsoft.Data.Sqlite;

namespace LarsonServer.MedalSync.Library;

/// <summary>What a library database's schema looks like, as read from it.</summary>
internal sealed record SchemaInfo(
    long UserVersion,
    string Fingerprint,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Columns,
    long? DefinitionsVersion)
{
    public bool IsSupported =>
        UserVersion == MedalSchema.SupportedUserVersion
        && MedalSchema.SupportedFingerprints.Contains(Fingerprint)
        && DefinitionsVersion == MedalSchema.SupportedDefinitionsVersion;
}

/// <summary>
/// The Medal library schema this build understands. Anything else is refused:
/// a future Medal schema needs a reviewed adapter, never a silent coercion.
/// </summary>
internal static partial class MedalSchema
{
    public const long SupportedUserVersion = 4;

    /// <summary><c>content_counts.__defsVersion</c>: the counter trigger definitions.</summary>
    public const long SupportedDefinitionsVersion = 3;

    /// <summary>
    /// SHA-256 of the normalized schema of Medal 2643.583.1 (recorder 2643.3040.1),
    /// read from the owner's library on 2026-10-24. The test fixture reproduces it.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedFingerprints = new HashSet<string>(StringComparer.Ordinal)
    {
        "5d696e3f51fe0b52494cd0c50b011540f7f2db117295ba11b9226579fdea6420",
    };

    /// <summary>Tables that make up the shared library, in canonical order.</summary>
    public static readonly IReadOnlyList<string> LibraryTables = ["contents", "deleted_contents", "content_counts"];

    /// <summary>Machine-specific settings, account state and hardware IDs. Never transferred.</summary>
    public const string DeviceTable = "key_values";

    public const string DefinitionsVersionKey = "__defsVersion";

    /// <summary>The JSONB column of a library table, if it has one.</summary>
    public static string? JsonColumn(string table) => table switch
    {
        "contents" => "metadata",
        "deleted_contents" => "blob",
        _ => null,
    };

    /// <summary>The unique, non-null column that identifies a row.</summary>
    public static string NaturalKey(string table) => table switch
    {
        "contents" => "local_content_id",
        "deleted_contents" => "local_content_id",
        "content_counts" => "key",
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Not a library table."),
    };

    public static SchemaInfo Read(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        long userVersion = Scalar<long>(connection, transaction, "PRAGMA user_version");
        var entries = new List<(string Type, string Name, string Table, string Sql)>();
        using (var command = Command(connection, transaction,
            "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                entries.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        var columns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (string table in LibraryTables.Append(DeviceTable))
        {
            var names = new List<string>();
            using var command = Command(connection, transaction, $"SELECT name FROM pragma_table_info({Literal(table)})");
            using var reader = command.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
            columns[table] = names;
        }
        long? definitions = null;
        if (columns["content_counts"].Count > 0)
        {
            using var command = Command(connection, transaction,
                "SELECT count FROM content_counts WHERE key = $key");
            command.Parameters.AddWithValue("$key", DefinitionsVersionKey);
            object? value = command.ExecuteScalar();
            definitions = value is long number ? number : null;
        }
        return new SchemaInfo(userVersion, ComputeFingerprint(userVersion, entries), columns, definitions);
    }

    public static void RequireSupported(SchemaInfo schema, string where)
    {
        if (schema.IsSupported) return;
        throw new IncompatibleLibraryException(
            $"The Medal library at {where} uses an unsupported schema (user_version {schema.UserVersion}, "
            + $"definitions {schema.DefinitionsVersion?.ToString() ?? "missing"}, fingerprint {schema.Fingerprint}). "
            + "Medal has probably been updated. Nothing was changed; update and retest the medal-sync schema adapter first.");
    }

    /// <summary>
    /// Fingerprint: SHA-256 of the user version and every schema object, sorted
    /// by type then name, with whitespace runs collapsed. Indexes are included
    /// so any schema change at all is noticed.
    /// </summary>
    internal static string ComputeFingerprint(long userVersion, IEnumerable<(string Type, string Name, string Table, string Sql)> entries)
    {
        var text = new StringBuilder();
        text.Append(userVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var entry in entries
            .OrderBy(e => e.Type, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            text.Append('\n').Append(entry.Type).Append('\t').Append(entry.Name).Append('\t')
                .Append(entry.Table).Append('\t').Append(Whitespace().Replace(entry.Sql, " ").Trim());
        }
        return Hashing.Sha256Hex(Encoding.UTF8.GetBytes(text.ToString()));
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    internal static T Scalar<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        object? value = command.ExecuteScalar();
        return value is T typed ? typed : throw new InvalidDataException($"'{sql}' returned {value ?? "null"}.");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

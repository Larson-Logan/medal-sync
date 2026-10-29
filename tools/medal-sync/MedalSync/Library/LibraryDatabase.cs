using System.Text;
using MedalSync.Util;
using Microsoft.Data.Sqlite;

namespace MedalSync.Library;

/// <summary>Opens Medal library databases and reads consistent snapshots from them.</summary>
internal static class LibraryDatabase
{
    /// <summary>Oldest SQLite release with the JSONB functions the schema relies on.</summary>
    private static readonly Version MinimumSqlite = new(3, 45, 0);

    public static SqliteConnection OpenReadOnly(string path) => Open(path, SqliteOpenMode.ReadOnly);

    public static SqliteConnection OpenReadWrite(string path) => Open(path, SqliteOpenMode.ReadWrite);

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"No Medal library database at '{path}'.", path);
        // Pooling would keep the file open after Dispose, which must never happen
        // to a database Medal is about to open.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        try
        {
            RequireSqlite(connection);
            Execute(connection, null, "PRAGMA busy_timeout = 0");
            if (mode == SqliteOpenMode.ReadOnly) Execute(connection, null, "PRAGMA query_only = ON");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static string SqliteVersion(SqliteConnection connection) =>
        MedalSchema.Scalar<string>(connection, null, "SELECT sqlite_version()");

    private static void RequireSqlite(SqliteConnection connection)
    {
        string text = SqliteVersion(connection);
        if (!Version.TryParse(text, out Version? version) || version < MinimumSqlite)
        {
            throw new IncompatibleLibraryException($"SQLite {text} is too old for Medal's JSONB library; {MinimumSqlite} or newer is required.");
        }
    }

    /// <summary>Reads the library tables inside one read transaction.</summary>
    public static LibrarySnapshot ReadSnapshot(string path, string accountId)
    {
        using var connection = OpenReadOnly(path);
        using var transaction = connection.BeginTransaction(deferred: true);
        var schema = MedalSchema.Read(connection, transaction);
        MedalSchema.RequireSupported(schema, path);
        return ReadSnapshot(connection, transaction, accountId, schema);
    }

    internal static LibrarySnapshot ReadSnapshot(SqliteConnection connection, SqliteTransaction transaction, string accountId, SchemaInfo schema)
    {
        var tables = MedalSchema.LibraryTables.Select(t => ReadTable(connection, transaction, t, schema.Columns[t])).ToList();
        return LibrarySnapshot.Create(accountId, schema.UserVersion, schema.Fingerprint, tables);
    }

    private static TableSnapshot ReadTable(SqliteConnection connection, SqliteTransaction transaction, string table, IReadOnlyList<string> columns)
    {
        string? json = MedalSchema.JsonColumn(table);
        var select = columns.Select(c =>
        {
            string quoted = MedalSchema.Quote(c);
            string value = c == json ? $"CASE WHEN typeof({quoted}) = 'blob' THEN json({quoted}) END" : quoted;
            // The storage class as a number: reading it allocates nothing, unlike typeof()'s text for every cell.
            return $"CASE typeof({quoted}) WHEN 'null' THEN {TypeNull} WHEN 'integer' THEN {TypeInteger} WHEN 'text' THEN {TypeText} "
                + $"WHEN 'blob' THEN {TypeBlob} ELSE {TypeReal} END, {value}";
        });
        var rows = new List<IReadOnlyList<Cell?>>();
        try
        {
            using var command = MedalSchema.Command(connection, transaction,
                $"SELECT {string.Join(", ", select)} FROM {MedalSchema.Quote(table)} ORDER BY {MedalSchema.Quote(MedalSchema.NaturalKey(table))}");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Cell?[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    row[i] = ReadCell(reader, i * 2, table, columns[i], columns[i] == json);
                }
                rows.Add(row);
            }
        }
        catch (SqliteException ex)
        {
            throw new IncompatibleLibraryException($"Could not read {table}: {ex.Message}", ex);
        }
        return new TableSnapshot(table, columns.ToArray(), rows);
    }

    private const int TypeNull = 0, TypeInteger = 1, TypeText = 2, TypeBlob = 3, TypeReal = 4;
    private static readonly string[] TypeNames = ["null", "integer", "text", "blob", "real"];

    private static Cell? ReadCell(SqliteDataReader reader, int typeOrdinal, string table, string column, bool isJson)
    {
        int type = reader.GetInt32(typeOrdinal);
        int value = typeOrdinal + 1;
        return (type, isJson) switch
        {
            (TypeNull, _) => null,
            (TypeInteger, false) => IntegerCell.Of(reader.GetInt64(value)),
            (TypeText, false) => new TextCell(reader.GetString(value)),
            (TypeBlob, true) => new JsonbCell(reader.GetString(value)),
            _ => throw new IncompatibleLibraryException(
                $"{table}.{column} holds a {TypeNames[type]} value this adapter does not transfer. Nothing was changed."),
        };
    }

    /// <summary>A digest of <c>key_values</c>, used to prove device settings were untouched.</summary>
    public static string DeviceSettingsDigest(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var text = new StringBuilder();
        using var command = MedalSchema.Command(connection, transaction,
            $"SELECT key, hex(value), typeof(value) FROM {MedalSchema.Quote(MedalSchema.DeviceTable)} ORDER BY key");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            text.Append(reader.GetString(0)).Append('\t').Append(reader.GetString(2)).Append('\t').Append(reader.GetString(1)).Append('\n');
        }
        return Hashing.Sha256Hex(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>The JSON text of one <c>key_values</c> entry, or null when absent.</summary>
    public static string? ReadSetting(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var command = MedalSchema.Command(connection, transaction,
            $"SELECT json(value) FROM {MedalSchema.Quote(MedalSchema.DeviceTable)} WHERE key = $key");
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = MedalSchema.Command(connection, transaction, sql);
        command.ExecuteNonQuery();
    }
}

using Microsoft.Data.Sqlite;

namespace MedalSync.Library;

internal sealed record RestoreOptions(
    string ExpectedAccountId,
    string BackupDirectory,
    int BackupsToKeep = 10,
    bool AllowEmptyReplacement = false);

internal sealed record RestoreResult(bool Changed, string? BackupPath, string Digest, int Contents, int DeletedContents, int Counts);

/// <summary>
/// Replaces the library tables of a stopped Medal database with a snapshot.
/// One transaction: everything is verified before commit, or nothing changes.
/// <c>key_values</c> (device settings, login, hardware IDs) is never written.
/// </summary>
internal static class LibraryRestorer
{
    private const string InUseMessage = "Another program is using the Medal library. Quit Medal from its tray icon and try again. Nothing was changed.";

    public static RestoreResult Restore(string databasePath, LibrarySnapshot snapshot, RestoreOptions options, DateTimeOffset now)
    {
        if (snapshot.AccountId != options.ExpectedAccountId)
        {
            throw new IncompatibleLibraryException(
                $"The snapshot belongs to Medal account {snapshot.AccountId}, but this PC's library is account {options.ExpectedAccountId}. Nothing was changed.");
        }
        RequireNoOtherProcess(databasePath);
        using var connection = LibraryDatabase.OpenReadWrite(databasePath);
        RequireNoOtherUsers(connection);
        var schema = MedalSchema.Read(connection);
        MedalSchema.RequireSupported(schema, databasePath);
        if (schema.Fingerprint != snapshot.SchemaFingerprint || schema.UserVersion != snapshot.SchemaVersion)
        {
            throw new IncompatibleLibraryException(
                "The snapshot was taken from a different Medal schema than this PC's. Update Medal on both PCs to the same version first. Nothing was changed.");
        }
        RequireCompatibleSnapshot(snapshot, schema);

        string digest = snapshot.Digest();
        string current;
        int currentContents;
        using (var read = connection.BeginTransaction(deferred: true))
        {
            var existing = LibraryDatabase.ReadSnapshot(connection, read, options.ExpectedAccountId, schema);
            current = existing.Digest();
            currentContents = existing.RowCount("contents");
        }
        if (current == digest)
        {
            return new RestoreResult(false, null, digest, snapshot.RowCount("contents"),
                snapshot.RowCount("deleted_contents"), snapshot.RowCount("content_counts"));
        }
        if (snapshot.RowCount("contents") == 0 && currentContents > 0 && !options.AllowEmptyReplacement)
        {
            throw new UnsafeOperationException(
                $"The snapshot has no library entries but this PC has {currentContents}. An empty snapshot is never applied implicitly. Nothing was changed.");
        }

        string backup = DatabaseBackup.Create(connection, options.BackupDirectory, options.ExpectedAccountId, now);
        using (var write = connection.BeginTransaction(deferred: false))
        {
            string devices = LibraryDatabase.DeviceSettingsDigest(connection, write);
            Replace(connection, write, snapshot);
            Verify(connection, write, snapshot, schema, digest, devices);
            write.Commit();
        }
        LibraryDatabase.Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE)");
        DatabaseBackup.Prune(options.BackupDirectory, options.ExpectedAccountId, options.BackupsToKeep);
        return new RestoreResult(true, backup, digest, snapshot.RowCount("contents"),
            snapshot.RowCount("deleted_contents"), snapshot.RowCount("content_counts"));
    }

    /// <summary>
    /// SQLite keeps the database file open for as long as a connection exists,
    /// even an idle one, so an exclusive open fails while Medal has the library
    /// open at all. Retries ride out a brief scan by antivirus or the indexer.
    /// </summary>
    private static void RequireNoOtherProcess(string databasePath)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var probe = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                if (attempt == 5) throw new UnsafeOperationException(InUseMessage);
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    /// <summary>
    /// A truncating checkpoint reports "busy" when another connection is inside a
    /// transaction: a second check that also covers a connection opened just
    /// after the exclusive probe.
    /// </summary>
    private static void RequireNoOtherUsers(SqliteConnection connection)
    {
        try
        {
            using var command = MedalSchema.Command(connection, null, "PRAGMA wal_checkpoint(TRUNCATE)");
            using var reader = command.ExecuteReader();
            if (reader.Read() && reader.GetInt64(0) != 0) throw new UnsafeOperationException(InUseMessage);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw new UnsafeOperationException(InUseMessage);
        }
    }

    private static void RequireCompatibleSnapshot(LibrarySnapshot snapshot, SchemaInfo schema)
    {
        foreach (var table in snapshot.Tables)
        {
            if (!table.Columns.SequenceEqual(schema.Columns[table.Name]))
            {
                throw new IncompatibleLibraryException($"The snapshot's {table.Name} columns do not match this library. Nothing was changed.");
            }
        }
        var counts = snapshot.Table("content_counts");
        int keyIndex = counts.ColumnIndex("key");
        int countIndex = counts.ColumnIndex("count");
        var definitions = counts.Rows.FirstOrDefault(r => TableSnapshot.Text(r[keyIndex]) == MedalSchema.DefinitionsVersionKey);
        if (definitions?[countIndex] is not IntegerCell { Value: MedalSchema.SupportedDefinitionsVersion })
        {
            throw new IncompatibleLibraryException("The snapshot's counter definitions version is missing or unsupported. Nothing was changed.");
        }
    }

    private static void Replace(SqliteConnection connection, SqliteTransaction transaction, LibrarySnapshot snapshot)
    {
        // Deleting content_counts first turns the insert triggers' counter
        // updates into no-ops; the snapshot's own counters are inserted last.
        foreach (string table in new[] { "contents", "deleted_contents", "content_counts" })
        {
            LibraryDatabase.Execute(connection, transaction, $"DELETE FROM {MedalSchema.Quote(table)}");
        }
        foreach (string table in MedalSchema.LibraryTables) Insert(connection, transaction, snapshot.Table(table));
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, TableSnapshot table)
    {
        string? json = MedalSchema.JsonColumn(table.Name);
        var names = table.Columns.Select(MedalSchema.Quote);
        var values = table.Columns.Select((c, i) => c == json ? $"jsonb($p{i})" : $"$p{i}");
        using var command = MedalSchema.Command(connection, transaction,
            $"INSERT INTO {MedalSchema.Quote(table.Name)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", values)})");
        var parameters = table.Columns.Select((_, i) => command.Parameters.Add($"$p{i}", SqliteType.Text)).ToArray();
        command.Prepare();
        foreach (var row in table.Rows)
        {
            for (int i = 0; i < row.Count; i++)
            {
                (parameters[i].SqliteType, parameters[i].Value) = Bind(row[i], table.Columns[i] == json, table.Name, table.Columns[i]);
            }
            command.ExecuteNonQuery();
        }
    }

    private static (SqliteType Type, object Value) Bind(Cell? cell, bool isJson, string table, string column) => cell switch
    {
        null => (SqliteType.Text, DBNull.Value),
        IntegerCell n when !isJson => (SqliteType.Integer, n.Value),
        TextCell t when !isJson => (SqliteType.Text, t.Value),
        JsonbCell j when isJson => (SqliteType.Text, j.Json),
        _ => throw new InvalidDataException($"{table}.{column} cannot hold {cell.GetType().Name}."),
    };

    private static void Verify(SqliteConnection connection, SqliteTransaction transaction, LibrarySnapshot snapshot,
        SchemaInfo schema, string digest, string devicesBefore)
    {
        var written = LibraryDatabase.ReadSnapshot(connection, transaction, snapshot.AccountId, schema);
        if (written.Digest() != digest)
        {
            throw new InvalidDataException("The restored library does not read back identically to the snapshot. The change was rolled back.");
        }
        if (LibraryDatabase.DeviceSettingsDigest(connection, transaction) != devicesBefore)
        {
            throw new InvalidDataException("Device settings changed during the restore. The change was rolled back.");
        }
        string integrity = MedalSchema.Scalar<string>(connection, transaction, "PRAGMA integrity_check");
        if (integrity != "ok") throw new InvalidDataException($"The restored library failed its integrity check ({integrity}). The change was rolled back.");
    }
}

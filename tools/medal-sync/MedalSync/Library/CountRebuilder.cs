using Microsoft.Data.Sqlite;

namespace MedalSync.Library;

/// <summary>
/// Recomputes <c>content_counts</c> by replaying a snapshot's rows through
/// Medal's own counter triggers in an in-memory copy of the schema. Used when a
/// snapshot's rows were edited (repairs); a normal handoff copies the counters.
/// </summary>
internal static class CountRebuilder
{
    public static LibrarySnapshot Rebuild(LibrarySnapshot snapshot, string schemaSourceDatabase)
    {
        IReadOnlyList<string> ddl = ReadDdl(schemaSourceDatabase);
        using var memory = new SqliteConnection("Data Source=:memory:;Pooling=False");
        memory.Open();
        using (var transaction = memory.BeginTransaction())
        {
            foreach (string statement in ddl) LibraryDatabase.Execute(memory, transaction, statement);
            var schema = MedalSchema.Read(memory, transaction);
            if (schema.Fingerprint != snapshot.SchemaFingerprint)
            {
                throw new IncompatibleLibraryException("The schema source does not match the snapshot's schema.");
            }
            var zeroed = Zeroed(snapshot.Table("content_counts"));
            var seeded = LibrarySnapshot.Create(snapshot.AccountId, snapshot.SchemaVersion, snapshot.SchemaFingerprint,
                [snapshot.Table("contents"), snapshot.Table("deleted_contents"), zeroed]);
            // Counters first, then rows, so each insert trigger increments them.
            InsertOrdered(memory, transaction, seeded);
            var rebuilt = LibraryDatabase.ReadSnapshot(memory, transaction, snapshot.AccountId,
                MedalSchema.Read(memory, transaction));
            return rebuilt;
        }
    }

    private static TableSnapshot Zeroed(TableSnapshot counts)
    {
        int key = counts.ColumnIndex("key");
        int count = counts.ColumnIndex("count");
        var rows = counts.Rows.Select(r =>
        {
            var copy = r.ToArray();
            if (TableSnapshot.Text(copy[key]) != MedalSchema.DefinitionsVersionKey) copy[count] = new IntegerCell(0);
            return (IReadOnlyList<Cell?>)copy;
        }).ToArray();
        return counts with { Rows = rows };
    }

    private static void InsertOrdered(SqliteConnection connection, SqliteTransaction transaction, LibrarySnapshot snapshot)
    {
        // Reuse the restorer's insert path by restoring into the empty in-memory
        // tables in the trigger-friendly order: counters, contents, deletions.
        LibraryRestorer.Insert(connection, transaction, snapshot.Table("content_counts"));
        LibraryRestorer.Insert(connection, transaction, snapshot.Table("contents"));
        LibraryRestorer.Insert(connection, transaction, snapshot.Table("deleted_contents"));
    }

    private static List<string> ReadDdl(string path)
    {
        using var source = LibraryDatabase.OpenReadOnly(path);
        var statements = new List<string> { $"PRAGMA user_version = {MedalSchema.Scalar<long>(source, null, "PRAGMA user_version")}" };
        using var command = MedalSchema.Command(source, null,
            "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' "
            + "ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END, name");
        using var reader = command.ExecuteReader();
        while (reader.Read()) statements.Add(reader.GetString(0));
        return statements;
    }
}

using Microsoft.Data.Sqlite;

namespace LarsonServer.MedalSync.Library;

/// <summary>Explicit recovery from Medal's full-database auto-restore. Restore
/// this PC's own saved settings only; never take them from a shared generation.</summary>
internal static class DeviceSettingsRestorer
{
    public static void Restore(string localDatabase, string originalDatabase, string accountId, string backupDirectory)
    {
        using var original = LibraryDatabase.OpenReadOnly(originalDatabase);
        using var local = LibraryDatabase.OpenReadWrite(localDatabase);
        var schema = MedalSchema.Read(local);
        MedalSchema.RequireSupported(schema, localDatabase);
        MedalSchema.RequireSupported(MedalSchema.Read(original), originalDatabase);
        string expectedSettings = LibraryDatabase.DeviceSettingsDigest(original, null);
        var values = new List<(string Key, object Value)>();
        using (var read = MedalSchema.Command(original, null, "SELECT key, value FROM key_values ORDER BY key"))
        using (var rows = read.ExecuteReader())
            while (rows.Read()) values.Add((rows.GetString(0), rows.GetValue(1)));
        if (values.Count == 0) throw new UnsafeOperationException("The original device settings are empty.");
        DatabaseBackup.Create(local, backupDirectory, accountId, DateTimeOffset.UtcNow);
        using var transaction = local.BeginTransaction(deferred: false);
        string library = LibraryDatabase.ReadSnapshot(local, transaction, accountId, schema).Digest();
        LibraryDatabase.Execute(local, transaction, "DELETE FROM key_values");
        foreach (var (key, value) in values)
        {
            using var insert = MedalSchema.Command(local, transaction, "INSERT INTO key_values(key,value) VALUES($key,$value)");
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }
        if (LibraryDatabase.DeviceSettingsDigest(local, transaction) != expectedSettings
            || LibraryDatabase.ReadSnapshot(local, transaction, accountId, schema).Digest() != library)
            throw new UnsafeOperationException("Device recovery failed readback; rolled back.");
        transaction.Commit();
    }
}

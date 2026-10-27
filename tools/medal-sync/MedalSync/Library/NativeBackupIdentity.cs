namespace MedalSync.Library;

/// <summary>
/// Medal schema-4 builds use application_id as a library lineage marker. A
/// different nonzero marker in capture/Databases makes Medal restore that
/// entire DB, including machine settings, over its local DB at startup.
/// Align only this header after the companion restored its verified library.
/// </summary>
internal static class NativeBackupIdentity
{
    public static bool Align(string localDatabase, string backupDatabase, string backupDirectory, string accountId)
    {
        var sidecars = InspectSidecars(backupDatabase);
        if (!File.Exists(backupDatabase)) return false;
        // SQLite opening a checkpointed WAL-mode DB read-only can still create
        // shared-memory/WAL sidecars. Inspect a verified private copy so reading
        // this compatibility marker never changes the native backup on SMB.
        Directory.CreateDirectory(backupDirectory);
        string inspected = Path.Combine(backupDirectory, "native-source-" + Guid.NewGuid().ToString("N") + ".db");
        string hash = Util.Hashing.Sha256File(backupDatabase);
        File.Copy(backupDatabase, inspected, overwrite: false);
        if (Util.Hashing.Sha256File(inspected) != hash || Util.Hashing.Sha256File(backupDatabase) != hash)
            throw new UnsafeOperationException("The native backup changed while being inspected; retained its private copy.");
        long expected;
        using (var backup = LibraryDatabase.OpenReadOnly(inspected))
        {
            MedalSchema.RequireSupported(MedalSchema.Read(backup), backupDatabase);
            if (MedalSchema.Scalar<string>(backup, null, "PRAGMA quick_check") != "ok")
                throw new UnsafeOperationException("Medal's native backup failed integrity validation.");
            expected = MedalSchema.Scalar<long>(backup, null, "PRAGMA application_id");
        }
        var after = InspectSidecars(backupDatabase);
        if (Util.Hashing.Sha256File(backupDatabase) != hash || sidecars.Count != after.Count
            || sidecars.Any(pair => !after.TryGetValue(pair.Key, out string? digest) || digest != pair.Value))
            throw new UnsafeOperationException("The native backup group changed while being inspected; retained its private copy.");
        if (expected == 0) return false; // Medal does not restore zero-ID backups.
        if (expected is < 0 or > int.MaxValue) throw new UnsafeOperationException("Unsupported native backup identity.");
        using var local = LibraryDatabase.OpenReadWrite(localDatabase);
        MedalSchema.RequireSupported(MedalSchema.Read(local), localDatabase);
        if (MedalSchema.Scalar<long>(local, null, "PRAGMA application_id") == expected) return false;
        DatabaseBackup.Create(local, backupDirectory, accountId, DateTimeOffset.UtcNow);
        using var transaction = local.BeginTransaction(deferred: false);
        var schema = MedalSchema.Read(local, transaction);
        string library = LibraryDatabase.ReadSnapshot(local, transaction, accountId, schema).Digest();
        string settings = LibraryDatabase.DeviceSettingsDigest(local, transaction);
        LibraryDatabase.Execute(local, transaction, "PRAGMA application_id = " + expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (MedalSchema.Scalar<long>(local, transaction, "PRAGMA application_id") != expected
            || LibraryDatabase.ReadSnapshot(local, transaction, accountId, schema).Digest() != library
            || LibraryDatabase.DeviceSettingsDigest(local, transaction) != settings)
            throw new UnsafeOperationException("Native backup alignment changed library or device records; rolled back.");
        transaction.Commit();
        return true;
    }

    private static Dictionary<string, string> InspectSidecars(string database)
    {
        foreach (string suffix in new[] { ".patch", ".patch.tmp", ".tmp" })
            if (File.Exists(database + suffix)) Pending(database + suffix);
        bool wal = File.Exists(database + "-wal"), shm = File.Exists(database + "-shm");
        // Medal leaves a checkpointed, zero-byte WAL plus its cached SHM index
        // after a normal quit. Neither contains database records to replay.
        // Never open or checkpoint that shared group: inspect the DB privately
        // and prove every source file stayed stable before touching local state.
        if ((wal || shm) && !File.Exists(database)) Pending(database);
        if (wal && new FileInfo(database + "-wal").Length != 0) Pending(database + "-wal");
        if (shm && !wal) Pending(database + "-shm");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string suffix in new[] { "-wal", "-shm" })
            if (File.Exists(database + suffix)) result.Add(suffix, Util.Hashing.Sha256File(database + suffix));
        return result;
    }

    private static void Pending(string path) =>
        throw new UnsafeOperationException("Medal's native backup has pending sidecars. Inspect it before starting Medal: " + path);
}

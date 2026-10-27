using LarsonServer.MedalSync.Library;
using LarsonServer.MedalSync.Tests.Fixtures;
using Microsoft.Data.Sqlite;

namespace LarsonServer.MedalSync.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private RestoreOptions Options(string machine = "second-pc") =>
        new(MedalFixture.AccountId, _fixture.PathFor(machine, "backups"));

    private string SourceWithClips()
    {
        string source = _fixture.CreateLibrary("first-pc");
        MedalFixture.AddContent(source, "clip-a", video: @"M:\Clips\Clips\Game\a.mp4", thumbnail: @"M:\Clips\Thumbnails\a.jpg",
            metadata: "{\"title\":\"Ace\",\"isFavorited\":1,\"tags\":[\"clutch\"],\"bookmarks\":[{\"t\":12.5}],\"contentInode\":\"42\"}");
        MedalFixture.AddContent(source, "clip-b", video: @"M:\Clips\Clips\Game\b.mp4", remoteId: "remote-b",
            metadata: "{\"title\":\"Ünïcode “quotes” \\\\ and \\\"escapes\\\"\",\"isFavorited\":false,\"n\":1.0e3}");
        MedalFixture.AddContent(source, "cloud-only", remoteId: "remote-c", metadata: "{\"title\":\"Cloud only\"}");
        return source;
    }

    [Fact]
    public void FixtureSchema_MatchesThePinnedFingerprint()
    {
        string library = _fixture.CreateLibrary("first-pc");
        using var connection = LibraryDatabase.OpenReadOnly(library);
        var schema = MedalSchema.Read(connection);
        Assert.Equal(4, schema.UserVersion);
        Assert.Contains(schema.Fingerprint, MedalSchema.SupportedFingerprints);
        Assert.True(schema.IsSupported);
    }

    [Fact]
    public void Snapshot_RoundTripsThroughCanonicalJson()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(SourceWithClips(), MedalFixture.AccountId);
        var parsed = LibrarySnapshot.FromCanonicalJson(snapshot.ToCanonicalJson());
        Assert.Equal(snapshot.Digest(), parsed.Digest());
        Assert.Equal(3, parsed.RowCount("contents"));
        Assert.Equal(snapshot.ToCanonicalJson(), parsed.ToCanonicalJson());
    }

    [Fact]
    public void Restore_CopiesLibraryAndKeepsDeviceSettings()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(SourceWithClips(), MedalFixture.AccountId);
        string destination = _fixture.CreateLibrary("second-pc");
        string settingsBefore = MedalFixture.Settings(destination);

        var result = LibraryRestorer.Restore(destination, snapshot, Options(), Now);

        Assert.True(result.Changed);
        Assert.Equal(snapshot.Digest(), LibraryDatabase.ReadSnapshot(destination, MedalFixture.AccountId).Digest());
        Assert.Equal(settingsBefore, MedalFixture.Settings(destination));
        Assert.Contains("second-pc-microphone", settingsBefore, StringComparison.Ordinal);
        Assert.Equal(1, MedalFixture.Count(destination, "favorites"));
        Assert.True(File.Exists(result.BackupPath));
        DatabaseBackup.Verify(result.BackupPath!);
        // A backup is one self-contained file, with no WAL side files to orphan.
        Assert.False(File.Exists(result.BackupPath + "-wal"));
        Assert.False(File.Exists(result.BackupPath + "-shm"));
    }

    [Fact]
    public void Restore_IsANoOpWhenTheLibraryAlreadyMatches()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(SourceWithClips(), MedalFixture.AccountId);
        string destination = _fixture.CreateLibrary("second-pc");
        LibraryRestorer.Restore(destination, snapshot, Options(), Now);

        var second = LibraryRestorer.Restore(destination, snapshot, Options(), Now.AddMinutes(1));

        Assert.False(second.Changed);
        Assert.Null(second.BackupPath);
        Assert.Single(DatabaseBackup.List(Options().BackupDirectory, MedalFixture.AccountId));
    }

    [Fact]
    public void Restore_RollsBackWhenAnInsertFails()
    {
        var good = LibraryDatabase.ReadSnapshot(SourceWithClips(), MedalFixture.AccountId);
        string destination = _fixture.CreateLibrary("second-pc");
        LibraryRestorer.Restore(destination, good, Options(), Now);
        // A second entry claiming clip-b's remote ID violates Medal's UNIQUE constraint
        // after earlier rows were already deleted and reinserted.
        var bad = WithExtraRow(good, "x-2", "remote-b");

        Assert.Throws<SqliteException>(() => LibraryRestorer.Restore(destination, bad, Options(), Now.AddMinutes(1)));
        Assert.Equal(good.Digest(), LibraryDatabase.ReadSnapshot(destination, MedalFixture.AccountId).Digest());
    }

    [Fact]
    public void Snapshot_RefusesDuplicateIdentities()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(SourceWithClips(), MedalFixture.AccountId);
        var contents = snapshot.Table("contents");
        var duplicated = contents with { Rows = [.. contents.Rows, contents.Rows[0]] };

        Assert.Throws<InvalidDataException>(() => LibrarySnapshot.Create(snapshot.AccountId, snapshot.SchemaVersion,
            snapshot.SchemaFingerprint, [duplicated, snapshot.Table("deleted_contents"), snapshot.Table("content_counts")]));
    }

    [Fact]
    public void Restore_RefusesAChangedSchemaWithoutTouchingTheDestination()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(SourceWithClips(), MedalFixture.AccountId);
        string destination = _fixture.CreateLibrary("second-pc");
        MedalFixture.AddContent(destination, "local-only");
        using (var connection = MedalFixture.Open(destination))
        {
            MedalFixture.Execute(connection, "ALTER TABLE contents ADD COLUMN future_column TEXT");
        }

        Assert.Throws<IncompatibleLibraryException>(() => LibraryRestorer.Restore(destination, snapshot, Options(), Now));
        Assert.Throws<IncompatibleLibraryException>(() => LibraryDatabase.ReadSnapshot(destination, MedalFixture.AccountId));
        Assert.False(Directory.Exists(Options().BackupDirectory));
    }

    [Fact]
    public void Restore_RefusesAnotherAccount()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(SourceWithClips(), MedalFixture.AccountId);
        string destination = _fixture.CreateLibrary("second-pc");

        var ex = Assert.Throws<IncompatibleLibraryException>(() =>
            LibraryRestorer.Restore(destination, snapshot, Options() with { ExpectedAccountId = "999" }, Now));
        Assert.Contains("account", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restore_NeverAppliesAnEmptySnapshotImplicitly()
    {
        var empty = LibraryDatabase.ReadSnapshot(_fixture.CreateLibrary("first-pc"), MedalFixture.AccountId);
        string destination = _fixture.CreateLibrary("second-pc");
        MedalFixture.AddContent(destination, "precious");

        Assert.Throws<UnsafeOperationException>(() => LibraryRestorer.Restore(destination, empty, Options(), Now));
        Assert.Equal(1, LibraryDatabase.ReadSnapshot(destination, MedalFixture.AccountId).RowCount("contents"));

        var forced = LibraryRestorer.Restore(destination, empty, Options() with { AllowEmptyReplacement = true }, Now);
        Assert.True(forced.Changed);
    }

    [Fact]
    public void Read_RefusesValueTypesItCannotTransfer()
    {
        string library = _fixture.CreateLibrary("first-pc");
        MedalFixture.AddContent(library, "real", createdAt: 1);
        using (var connection = MedalFixture.Open(library))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET created_at = 1.5");
        }

        Assert.Throws<IncompatibleLibraryException>(() => LibraryDatabase.ReadSnapshot(library, MedalFixture.AccountId));
    }

    [Fact]
    public void Read_RefusesJsonStoredAsText()
    {
        string library = _fixture.CreateLibrary("first-pc");
        MedalFixture.AddContent(library, "text-json");
        using (var connection = MedalFixture.Open(library))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET metadata = json(metadata)");
        }

        Assert.Throws<IncompatibleLibraryException>(() => LibraryDatabase.ReadSnapshot(library, MedalFixture.AccountId));
    }

    [Fact]
    public void Restore_CarriesDeletionsAndEditsForward()
    {
        string first = SourceWithClips();
        string second = _fixture.CreateLibrary("second-pc");
        LibraryRestorer.Restore(second, LibraryDatabase.ReadSnapshot(first, MedalFixture.AccountId), Options(), Now);
        // The second PC deletes one clip and renames another.
        MedalFixture.DeleteContent(second, "clip-a");
        using (var connection = MedalFixture.Open(second))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET metadata = jsonb_set(metadata, '$.title', 'Renamed') WHERE local_content_id = 'clip-b'");
        }
        var handBack = LibraryDatabase.ReadSnapshot(second, MedalFixture.AccountId);

        LibraryRestorer.Restore(first, handBack, Options("first-pc"), Now.AddHours(1));

        var result = LibraryDatabase.ReadSnapshot(first, MedalFixture.AccountId);
        Assert.Equal(handBack.Digest(), result.Digest());
        Assert.Equal(2, result.RowCount("contents"));
        Assert.Equal(1, result.RowCount("deleted_contents"));
        Assert.Equal(0, MedalFixture.Count(first, "favorites"));
        Assert.Contains("first-pc-microphone", MedalFixture.Settings(first), StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_RefusesWhileAnIdleConnectionHoldsTheLibraryOpen()
    {
        // Medal sitting idle in the tray: its connection is open but in no transaction.
        var snapshot = LibraryDatabase.ReadSnapshot(SourceWithClips(), MedalFixture.AccountId);
        string destination = _fixture.CreateLibrary("second-pc");
        MedalFixture.AddContent(destination, "local");
        var before = LibraryDatabase.ReadSnapshot(destination, MedalFixture.AccountId).Digest();

        using (MedalFixture.Open(destination))
        {
            Assert.Throws<UnsafeOperationException>(() => LibraryRestorer.Restore(destination, snapshot, Options(), Now));
        }

        Assert.Equal(before, LibraryDatabase.ReadSnapshot(destination, MedalFixture.AccountId).Digest());
        Assert.True(LibraryRestorer.Restore(destination, snapshot, Options(), Now).Changed);
    }

    [Fact]
    public void Restore_RefusesWhileAnotherConnectionIsReading()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(SourceWithClips(), MedalFixture.AccountId);
        string destination = _fixture.CreateLibrary("second-pc");
        MedalFixture.AddContent(destination, "local");
        using var medal = MedalFixture.Open(destination);
        using var reading = medal.BeginTransaction(deferred: true);
        using (var command = medal.CreateCommand())
        {
            command.Transaction = reading;
            command.CommandText = "SELECT COUNT(*) FROM contents";
            command.ExecuteScalar();
        }
        // A write by another connection leaves a WAL frame the reader still needs.
        using (var other = MedalFixture.Open(destination))
        {
            MedalFixture.Execute(other, "UPDATE contents SET created_at = created_at + 1");
        }

        Assert.Throws<UnsafeOperationException>(() => LibraryRestorer.Restore(destination, snapshot, Options(), Now));
    }

    [Fact]
    public void Prune_KeepsOnlyTheNewestBackups()
    {
        string library = _fixture.CreateLibrary("first-pc");
        string backups = _fixture.PathFor("backups");
        using (var connection = LibraryDatabase.OpenReadOnly(library))
        {
            for (int i = 0; i < 4; i++) DatabaseBackup.Create(connection, backups, MedalFixture.AccountId, Now.AddMinutes(i));
        }
        File.WriteAllText(Path.Combine(backups, "medal-library-12345-not-a-stamp.db"), "unrelated");

        var removed = DatabaseBackup.Prune(backups, MedalFixture.AccountId, keep: 2);

        Assert.Equal(2, removed.Count);
        Assert.Equal(2, DatabaseBackup.List(backups, MedalFixture.AccountId).Count);
        Assert.True(File.Exists(Path.Combine(backups, "medal-library-12345-not-a-stamp.db")));
    }

    [Fact]
    public void CountRebuilder_ReplaysMedalsTriggers()
    {
        string source = SourceWithClips();
        var snapshot = LibraryDatabase.ReadSnapshot(source, MedalFixture.AccountId);
        var rebuilt = CountRebuilder.Rebuild(snapshot, source);
        Assert.Equal(snapshot.Digest(), rebuilt.Digest());

        var stale = WithExtraRow(snapshot, "extra-favorite", null, "{\"isFavorited\":1}");
        var counts = CountRebuilder.Rebuild(stale, source).Table("content_counts");
        var favorites = counts.Rows.Single(r => TableSnapshot.Text(r[0]) == "favorites");
        Assert.Equal(new IntegerCell(2), favorites[1]);
    }

    internal static LibrarySnapshot WithExtraRow(LibrarySnapshot snapshot, string id, string? remoteId, string metadata = "{\"title\":\"x\"}")
    {
        var contents = snapshot.Table("contents");
        var row = new Cell?[contents.Columns.Count];
        row[contents.ColumnIndex("created_at")] = new IntegerCell(1);
        row[contents.ColumnIndex("category_id")] = new TextCell("game-1");
        row[contents.ColumnIndex("metadata")] = new JsonbCell(metadata);
        row[contents.ColumnIndex("local_content_id")] = new TextCell(id);
        if (remoteId is not null) row[contents.ColumnIndex("remote_content_id")] = new TextCell(remoteId);
        return LibrarySnapshot.Create(snapshot.AccountId, snapshot.SchemaVersion, snapshot.SchemaFingerprint,
            [contents with { Rows = [.. contents.Rows, row] }, snapshot.Table("deleted_contents"), snapshot.Table("content_counts")]);
    }
}

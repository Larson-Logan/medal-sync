using MedalSync.Library;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class NativeBackupTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("2643.583.1", "2643.3040.1", true)]
    [InlineData("2644.591.1", "2643.3040.1", true)]
    [InlineData("2644.591.2", "2643.3040.1", false)]
    [InlineData("2644.591.1", "2643.3040.2", false)]
    [InlineData(null, "2643.3040.1", false)]
    [InlineData("2644.591.1", null, false)]
    public void NativeBackupCompatibilityAcceptsOnlyReviewedExactBuildPairs(string? app, string? recorder, bool supported)
    {
        var versions = new MedalVersions(app, recorder);
        if (supported) Assert.Null(Record.Exception(() => NativeBackupBuilds.RequireSupported(versions)));
        else Assert.Throws<IncompatibleLibraryException>(() => NativeBackupBuilds.RequireSupported(versions));
    }

    [Fact]
    public void NativeBackupLineageIsAlignedWithoutReplacingLibraryOrDeviceSettings()
    {
        string local = _fixture.CreateLibrary("second"), remote = _fixture.CreateLibrary("first");
        MedalFixture.AddContent(local, "union");
        MedalFixture.AddContent(remote, "stale-backup");
        using (var db = MedalFixture.Open(local)) MedalFixture.Execute(db, "PRAGMA application_id=111");
        using (var db = MedalFixture.Open(remote)) { MedalFixture.Execute(db, "PRAGMA application_id=222"); MedalFixture.Execute(db, "PRAGMA journal_mode=DELETE"); }
        string library = LibraryDatabase.ReadSnapshot(local, MedalFixture.AccountId).Digest();
        string settings = MedalFixture.Settings(local);
        string backupBefore = LibraryDatabase.ReadSnapshot(remote, MedalFixture.AccountId).Digest();
        Assert.True(NativeBackupIdentity.Align(local, remote, _fixture.PathFor("backups"), MedalFixture.AccountId));
        using (var db = LibraryDatabase.OpenReadOnly(local)) Assert.Equal(222, MedalSchema.Scalar<long>(db, null, "PRAGMA application_id"));
        Assert.Equal(library, LibraryDatabase.ReadSnapshot(local, MedalFixture.AccountId).Digest());
        Assert.Equal(settings, MedalFixture.Settings(local));
        Assert.Equal(backupBefore, LibraryDatabase.ReadSnapshot(remote, MedalFixture.AccountId).Digest());
        Assert.False(NativeBackupIdentity.Align(local, remote, _fixture.PathFor("backups"), MedalFixture.AccountId));
        Assert.Single(DatabaseBackup.List(_fixture.PathFor("backups"), MedalFixture.AccountId));
        // Model Medal's exact native restore decision: a differing nonzero
        // application_id restores the WHOLE backup. It must now be false.
        using var l = LibraryDatabase.OpenReadOnly(local);
        using var r = LibraryDatabase.OpenReadOnly(remote);
        long backupId = MedalSchema.Scalar<long>(r, null, "PRAGMA application_id");
        Assert.False(backupId != 0 && backupId != MedalSchema.Scalar<long>(l, null, "PRAGMA application_id"));
    }

    [Theory]
    [InlineData(".patch")]
    [InlineData(".patch.tmp")]
    [InlineData(".tmp")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void IncompleteNativeBackupsBlockStartupWithoutChangingTheLocalDatabase(string suffix)
    {
        string local = _fixture.CreateLibrary("second"), remote = _fixture.PathFor("backup.db");
        File.WriteAllText(remote + suffix, "pending synthetic backup");
        string settings = MedalFixture.Settings(local);
        Assert.Throws<UnsafeOperationException>(() => NativeBackupIdentity.Align(local, remote, _fixture.PathFor("backups"), MedalFixture.AccountId));
        Assert.Equal(settings, MedalFixture.Settings(local));
    }

    [Fact]
    public void MissingAndZeroIdentityBackupsDoNotCauseALocalRewrite()
    {
        string local = _fixture.CreateLibrary("second"), zero = _fixture.CreateLibrary("zero");
        using (var db = MedalFixture.Open(zero)) MedalFixture.Execute(db, "PRAGMA journal_mode=DELETE");
        Assert.False(NativeBackupIdentity.Align(local, _fixture.PathFor("absent.db"), _fixture.PathFor("backups"), MedalFixture.AccountId));
        Assert.False(NativeBackupIdentity.Align(local, zero, _fixture.PathFor("backups"), MedalFixture.AccountId));
        Assert.Empty(DatabaseBackup.List(_fixture.PathFor("backups"), MedalFixture.AccountId));
    }

    [Fact]
    public void CheckpointedWalModeNativeBackupCanBeReadWithoutLeavingSidecars()
    {
        string local = _fixture.CreateLibrary("second"), remote = _fixture.CreateLibrary("native");
        using (var db = MedalFixture.Open(remote)) MedalFixture.Execute(db, "PRAGMA application_id=333");
        Assert.False(File.Exists(remote + "-wal"));
        Assert.True(NativeBackupIdentity.Align(local, remote, _fixture.PathFor("backups"), MedalFixture.AccountId));
        Assert.False(File.Exists(remote + "-wal"));
        Assert.False(File.Exists(remote + "-shm"));
    }

    [Fact]
    public void EmptyCheckpointedWalAndShmPermitStartupWithoutChangingTheSharedGroup()
    {
        string local = _fixture.CreateLibrary("second"), source = _fixture.CreateLibrary("native"), remote = _fixture.PathFor("native-copy.db");
        MedalFixture.AddContent(local, "union");
        string library = LibraryDatabase.ReadSnapshot(local, MedalFixture.AccountId).Digest();
        string settings = MedalFixture.Settings(local);
        using (var native = MedalFixture.Open(source))
        {
            MedalFixture.Execute(native, "PRAGMA journal_mode=WAL");
            MedalFixture.Execute(native, "PRAGMA application_id=444");
            MedalFixture.Execute(native, "PRAGMA wal_checkpoint(TRUNCATE)");
            PreserveStoppedImage(source, remote);
        }
        Assert.Equal(0, new FileInfo(remote + "-wal").Length);
        Assert.True(File.Exists(remote + "-shm"));
        string[] group = [remote, remote + "-wal", remote + "-shm"];
        var before = group.ToDictionary(p => p, Hashing.Sha256File);

        Assert.True(NativeBackupIdentity.Align(local, remote, _fixture.PathFor("backups"), MedalFixture.AccountId));

        Assert.All(group, path => Assert.Equal(before[path], Hashing.Sha256File(path)));
        Assert.Equal(library, LibraryDatabase.ReadSnapshot(local, MedalFixture.AccountId).Digest());
        Assert.Equal(settings, MedalFixture.Settings(local));
        using var db = LibraryDatabase.OpenReadOnly(local);
        Assert.Equal(444, MedalSchema.Scalar<long>(db, null, "PRAGMA application_id"));
    }

    [Fact]
    public void NonemptyRealWalBlocksStartupAndPreservesBothDatabases()
    {
        string local = _fixture.CreateLibrary("second"), source = _fixture.CreateLibrary("native"), remote = _fixture.PathFor("native-copy.db");
        string library = LibraryDatabase.ReadSnapshot(local, MedalFixture.AccountId).Digest();
        using (var native = MedalFixture.Open(source))
        {
            MedalFixture.Execute(native, "PRAGMA journal_mode=WAL");
            MedalFixture.Execute(native, "PRAGMA application_id=555");
            PreserveStoppedImage(source, remote);
        }
        Assert.True(new FileInfo(remote + "-wal").Length > 0);
        string wal = Hashing.Sha256File(remote + "-wal");

        Assert.Throws<UnsafeOperationException>(() => NativeBackupIdentity.Align(local, remote, _fixture.PathFor("backups"), MedalFixture.AccountId));

        Assert.Equal(wal, Hashing.Sha256File(remote + "-wal"));
        Assert.Equal(library, LibraryDatabase.ReadSnapshot(local, MedalFixture.AccountId).Digest());
        Assert.Empty(DatabaseBackup.List(_fixture.PathFor("backups"), MedalFixture.AccountId));
    }

    // SQLite normally removes sidecars when its last connection closes. Copy
    // the quiescent synthetic group to model files a stopped process left,
    // without weakening production's refusal to read a live Windows writer.
    private static void PreserveStoppedImage(string source, string target)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            using var input = new FileStream(source + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(target + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    [Fact]
    public void ExplicitDeviceRecoveryRestoresOnlyThisPcsSavedSettingsAndRetainsLibraryRows()
    {
        string own = _fixture.CreateLibrary("second"), clobbered = _fixture.CreateLibrary("first");
        MedalFixture.AddContent(own, "original");
        MedalFixture.AddContent(clobbered, "current-library");
        string current = LibraryDatabase.ReadSnapshot(clobbered, MedalFixture.AccountId).Digest();
        DeviceSettingsRestorer.Restore(clobbered, own, MedalFixture.AccountId, _fixture.PathFor("forensics"));
        Assert.Equal(MedalFixture.Settings(own), MedalFixture.Settings(clobbered));
        Assert.Equal(current, LibraryDatabase.ReadSnapshot(clobbered, MedalFixture.AccountId).Digest());
        Assert.Single(DatabaseBackup.List(_fixture.PathFor("forensics"), MedalFixture.AccountId));
    }
}

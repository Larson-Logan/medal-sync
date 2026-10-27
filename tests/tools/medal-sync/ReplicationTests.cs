using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class ReplicationTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();
    private static MedalSyncConfig Config() => new(1, "0123456789abcdef0123456789abcdef", "12345",
        @"\\server\Data\Coordination", @"\\server\Data\Videos", @"M:\Clips", @"E:\Cache", @"C:\Tools\rclone.exe",
        new MountConfig(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []);

    [Fact]
    public void FreshLocalStorageIsCreatedAndExistingLibraryIdentityIsNeverReplaced()
    {
        string folder = _fixture.PathFor("fresh-storage", "Clips");
        StorageIdentity.Initialize(folder, Config().LibraryId);
        string marker = Path.Combine(folder, MedalSyncConfig.StorageMarker);
        byte[] original = File.ReadAllBytes(marker);
        StorageIdentity.Initialize(folder, Config().LibraryId);
        Assert.Equal(original, File.ReadAllBytes(marker));
        Assert.Throws<UnsafeOperationException>(() => StorageIdentity.Initialize(folder, new string('a', 32)));
        Assert.Equal(original, File.ReadAllBytes(marker));
    }

    [Theory]
    [InlineData("onedrive:MedalSync", true)]
    [InlineData("dropbox:MedalSync/Media", true)]
    [InlineData("gdrive:Medal Sync", true)]
    [InlineData(":local:C:/secret", false)]
    [InlineData("C:/secret", false)]
    [InlineData("remote:", false)]
    [InlineData("remote:../secret", false)]
    [InlineData("remote:/", false)]
    [InlineData("remote:folder\nsecret", false)]
    [InlineData("remote:folder\\secret", false)]
    public void CloudDestinationsRequireAnExplicitNamedRemoteAndFolder(string remote, bool valid) =>
        Assert.Equal(valid, ReplicationConfig.ValidRemote(remote));

    [Fact]
    public void LocalFirstMountUsesPersistentStorageAndKeepsSessionCacheSeparate()
    {
        var config = Config() with { Replication = new(@"E:\MedalLibrary", null, []) };
        config.Validate();
        Assert.Equal(@"E:\MedalLibrary", config.MountSettings(new string('1', 32), @"C:\logs\mount.log").Remote);
        Assert.Equal(@"E:\MedalLibrary\Clips", config.MountedCapture.Display);
        Assert.Equal(@"\\server\Data\Videos\Clips", config.ServerCapture.Display);
    }

    [Theory]
    [InlineData(@"E:\Cache")]
    [InlineData(@"E:\Cache\persistent")]
    [InlineData(@"E:\")]
    [InlineData(@"M:\Persistent")]
    [InlineData(@"\\server\Data\Persistent")]
    public void LocalStorageCannotOverlapCacheOrUseTheMountOrShare(string local)
    {
        var config = Config() with { Replication = new(local, null, []) };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Theory]
    [InlineData(@"E:\Cache", @"C:\State")]
    [InlineData(@"E:\Library", @"E:\Library\Journal")]
    [InlineData(@"M:\Local", @"C:\State")]
    [InlineData(@"\\server\Data\Library", @"C:\State")]
    public void LocalModeRequiresSeparateLocalStorageAndJournal(string remote, string journal)
    {
        var config = Config() with { StorageMode = StorageMode.Local, Remote = remote, CoordinationRoot = journal };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void BandwidthLimitIsOptionalAndBounded()
    {
        var plan = ReplicationPlan.Arguments("copy", "source", "dest", "list", "config", 5);
        Assert.Equal("5Mi", plan[plan.ToList().IndexOf("--bwlimit") + 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplicationPlan.Arguments("copy", "source", "dest", "list", "config", 0));
    }

    [Fact]
    public void VersionedConflictPolicyKeepsBackupsAndByteVerification()
    {
        var copy = ReplicationPlan.Arguments("copy", "source", "dest", "list", "config", versionDirectory: "dest/.medal-sync-history/attempt");
        Assert.Contains("--backup-dir", copy);
        Assert.DoesNotContain("--immutable", copy);
        Assert.Contains("--checksum", copy);
        var check = ReplicationPlan.Arguments("check", "source", "dest", "list", "config", versionDirectory: "dest/.medal-sync-history/attempt");
        Assert.Contains("--download", check);
        Assert.DoesNotContain("--backup-dir", check);
    }

    [Fact]
    public void CloudCredentialsMustBeExplicitAndReplicaFoldersCannotOverlap()
    {
        var targets = new[] { new ReplicaTarget("one", "onedrive:MedalSync"), new ReplicaTarget("two", "onedrive:MedalSync/child") };
        Assert.Throws<InvalidDataException>((Config() with { Replication = new(null, null, [targets[0]]) }).Validate);
        Assert.Throws<InvalidDataException>((Config() with { Replication = new(null, @"C:\Private\rclone.conf", targets) }).Validate);
        var valid = Config() with { Replication = new(null, @"C:\Private\rclone.conf", [targets[0]]) };
        valid.Validate();
        Assert.EndsWith("/" + valid.LibraryId, ReplicationPlan.Destination(targets[0], valid.LibraryId));
    }

    [Theory]
    [InlineData(@"E:\Cache\rclone.conf")]
    [InlineData(@"E:\Persistent\rclone.conf")]
    public void CredentialsCannotBeStoredInsideMediaOrDisposableCache(string credentials)
    {
        var config = Config() with { Replication = new(@"E:\Persistent", credentials, [new ReplicaTarget("cloud", "onedrive:MedalSync")]) };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void CommandsAreAdditiveImmutableAndVerifyBytes()
    {
        var copy = ReplicationPlan.Arguments("copy", @"E:\Clips", "onedrive:MedalSync/id", "list", "credentials");
        Assert.Contains("--files-from-raw", copy);
        Assert.Contains("--immutable", copy);
        Assert.DoesNotContain("sync", copy);
        var check = ReplicationPlan.Arguments("check", @"E:\Clips", "onedrive:MedalSync/id", "list", "credentials");
        Assert.Contains("--download", check);
        Assert.Contains("--one-way", check);
        Assert.Throws<ArgumentException>(() => ReplicationPlan.Arguments("sync", "source", "target", "list", "config"));
    }

    [Fact]
    public void LocalFirstAdmissionBudgetsBothCopiesOnASharedVolume()
    {
        var inputs = new CapacityInputs(200_000_000, 10_000_000, 8, TimeSpan.FromSeconds(100), 0);
        Assert.True(CapacityAdmission.Evaluate(inputs).Admitted);
        var combined = Assert.Single(CapacityAdmission.WithPersistentCopy(inputs, 200_000_000, sameVolume: true));
        Assert.False(combined.Admitted);
        Assert.Equal(210_000_000, combined.RequiredBytes);
        Assert.Equal(10_000_000, combined.DeficitBytes);
    }

    [Fact]
    public void LocalFirstAdmissionChecksPersistentVolumeEvenWithEnoughCacheSpace()
    {
        var inputs = new CapacityInputs(1_000_000_000, 10_000_000, 8, TimeSpan.FromSeconds(100), 0);
        var verdicts = CapacityAdmission.WithPersistentCopy(inputs, 50_000_000, sameVolume: false);
        Assert.True(verdicts[0].Admitted);
        Assert.False(verdicts[1].Admitted);
    }

    private sealed class FakeProbe(bool exists = true, bool reparse = false) : IFileProbe
    {
        public FileProbeResult Probe(WindowsPath path, WindowsPath root) => new(exists, false, 10, DateTime.UnixEpoch, reparse);
    }

    [Fact]
    public void FileListContainsOnlyReferencedMediaIncludingLiteralHashNames()
    {
        string db = _fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "a", video: @"M:\Clips\#clip.mp4", metadata: """{"unknown":"C:\\private\\secret.txt"}""");
        MedalFixture.AddContent(db, "b", video: @"M:\Clips\#clip.mp4");
        var files = ReplicationPlan.Files(LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId), WindowsPath.Parse(@"M:\Clips"), WindowsPath.Parse(@"E:\Media"), new FakeProbe());
        Assert.Equal(new[] { "#clip.mp4" }, files);
    }

    [Fact]
    public void KnownKeyboardOverlayJsonIsCopiedWhenPresentAndSkippedWhenMissing()
    {
        string db = _fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "a", metadata: """{"keyboardOverlayFilePath":"M:\\Clips\\Temp\\InputActions\\a.json"}""");
        var snapshot = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        var capture = WindowsPath.Parse(@"M:\Clips");
        var source = WindowsPath.Parse(@"E:\Media");
        Assert.Equal(new[] { "Temp/InputActions/a.json" }, ReplicationPlan.Files(snapshot, capture, source, new FakeProbe()));
        Assert.Empty(ReplicationPlan.Files(snapshot, capture, source, new FakeProbe(exists: false)));
    }

    [Theory]
    [InlineData(@"C:\Private\secret.mp4", true, false)]
    [InlineData(@"M:\Clips\Databases\backup.db", true, false)]
    [InlineData(@"M:\Clips\secret.conf", true, false)]
    [InlineData(@"M:\Clips\clip.mp4", false, false)]
    [InlineData(@"M:\Clips\clip.mp4", true, true)]
    public void UnsafeOrMissingMediaCannotBeReplicated(string path, bool exists, bool reparse)
    {
        string db = _fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "a", video: path);
        Assert.ThrowsAny<Exception>(() => ReplicationPlan.Files(LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId),
            WindowsPath.Parse(@"M:\Clips"), WindowsPath.Parse(@"E:\Media"), new FakeProbe(exists, reparse)));
    }

    private MedalSyncConfig SyntheticStorage()
    {
        string remote = _fixture.PathFor("server");
        string serverClips = Path.Combine(remote, "Clips");
        Directory.CreateDirectory(serverClips);
        File.WriteAllText(Path.Combine(serverClips, MedalSyncConfig.StorageMarker), Config().LibraryId);
        File.WriteAllText(Path.Combine(serverClips, "clip.mp4"), "synthetic media bytes");
        // Both trees are disposable local directories. No real share or provider is used.
        return Config() with { Remote = remote, Replication = new(_fixture.PathFor("persistent"), null, []) };
    }

    [Fact]
    public void PersistentLocalPreparationUsesExactFileListAndPublishingWritesReceiptOnlyAfterCheck()
    {
        var config = SyntheticStorage();
        var state = new LocalState(_fixture.PathFor("state"));
        string db = _fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "a", video: @"M:\Clips\clip.mp4");
        var snapshot = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        List<string> commands = [];
        var replication = new StorageReplication(config, state, () => { }, arguments =>
        {
            commands.Add(arguments[0]);
            int listOption = arguments.ToList().IndexOf("--files-from-raw");
            Assert.Equal(new[] { "clip.mp4" }, File.ReadAllLines(arguments[listOption + 1]));
            Assert.False(File.Exists(Path.Combine(state.Root, "replication", "last-verified.json")));
            if (arguments[0] == "copy" && arguments[1] == config.ServerCapture.Display)
                File.Copy(Path.Combine(arguments[1], "clip.mp4"), Path.Combine(arguments[2], "clip.mp4"));
        });
        replication.Prepare(snapshot);
        Assert.Equal(config.LibraryId, File.ReadAllText(config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display).Trim());
        replication.Publish(snapshot);
        Assert.Equal(new[] { "copy", "check", "copy", "check" }, commands);
        Assert.True(File.Exists(Path.Combine(state.Root, "replication", "last-verified.json")));
        Assert.True(File.Exists(Path.Combine(config.MountedCapture.Display, "clip.mp4")));
        Assert.Empty(Directory.GetFiles(state.Root, "replication-*.txt"));
    }

    [Fact]
    public void FailedCopyKeepsLocalMediaAndDoesNotWriteVerificationReceipt()
    {
        var config = SyntheticStorage();
        Directory.CreateDirectory(config.MountedCapture.Display);
        File.Copy(config.ServerCapture.Append(MedalSyncConfig.StorageMarker).Display, config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display);
        File.WriteAllText(config.MountedCapture.Append("clip.mp4").Display, "synthetic media");
        var state = new LocalState(_fixture.PathFor("state"));
        string db = _fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "a", video: @"M:\Clips\clip.mp4");
        var replication = new StorageReplication(config, state, () => { }, _ => throw new IOException("provider unavailable"));
        Assert.Throws<IOException>(() => replication.Publish(LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId)));
        Assert.True(File.Exists(config.MountedCapture.Append("clip.mp4").Display));
        Assert.False(File.Exists(Path.Combine(state.Root, "replication", "last-verified.json")));
        Assert.Empty(Directory.GetFiles(state.Root, "replication-*.txt"));
    }

    [Fact]
    public void PreparationRefusesAnUnrelatedExistingLocalCollection()
    {
        var config = SyntheticStorage();
        Directory.CreateDirectory(config.MountedCapture.Display);
        File.WriteAllText(config.MountedCapture.Append("unrelated.txt").Display, "keep me");
        string db = _fixture.CreateLibrary("source");
        var replication = new StorageReplication(config, new LocalState(_fixture.PathFor("state")), () => { }, _ => Assert.Fail("Must not copy"));
        Assert.ThrowsAny<IOException>(() => replication.Prepare(LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId)));
        Assert.Equal("keep me", File.ReadAllText(config.MountedCapture.Append("unrelated.txt").Display));
    }
}

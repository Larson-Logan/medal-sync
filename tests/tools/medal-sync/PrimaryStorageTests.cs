using System.Text.Json;
using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class PrimaryStorageTests : IDisposable
{
    private readonly MedalFixture fixture = new();
    public void Dispose() => fixture.Dispose();
    private MedalSyncConfig Config() => new(1, new string('a', 32), MedalFixture.AccountId,
        @"\\server\share\Journal", "sftp-home:Media", @"M:\Clips", @"E:\Cache", @"C:\Tools\rclone.exe",
        new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, [])
    {
        Replication = new(fixture.PathFor("local"), fixture.PathFor("private", "rclone.conf"), []),
    };

    private LibrarySnapshot Snapshot()
    {
        string db = fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "clip", video: @"M:\Clips\clip.mp4");
        return LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
    }

    private static void Claim(MedalSyncConfig config)
    {
        Directory.CreateDirectory(config.MountedCapture.Display);
        File.WriteAllText(config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display, config.LibraryId);
    }

    private sealed class Primary(bool offline = false, bool present = true) : IAssetChecker
    {
        public void RequireServer() { if (offline) throw new StorageOfflineException("Synthetic offline primary"); }
        public bool ExistsOnServer(string path) => present;
        public AssetReport ThroughMount(LibrarySnapshot snapshot) => throw new NotSupportedException();
        public AssetReport OnServer(LibrarySnapshot snapshot) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("onedrive:MedalSync")]
    [InlineData("gdrive:Medal")]
    [InlineData("sftp-home:Media/Videos")]
    [InlineData("r2:medal-sync")]
    public void NamedPrimaryRequiresLocalRecordingAndExplicitCredentials(string remote)
    {
        var config = Config() with { Remote = remote };
        config.Validate();
        Assert.True(config.PrimaryIsRclone);
        Assert.Equal(remote + "/Clips", config.PrimaryCapture);
        Assert.Equal(config.Replication!.LocalCopyRoot, config.MountRemote);
        Assert.Throws<InvalidDataException>((config with { Replication = null }).Validate);
        Assert.Throws<InvalidDataException>((config with { Replication = config.Replication with { LocalCopyRoot = null } }).Validate);
        Assert.Throws<InvalidDataException>((config with { Replication = config.Replication with { RcloneConfigPath = null } }).Validate);
        Assert.Throws<InvalidDataException>((config with { StorageMode = StorageMode.Local }).Validate);
        Assert.Throws<UnsafeOperationException>(() => config.ServerCapture);
        var loaded = Json.Deserialize<MedalSyncConfig>(Json.Serialize(config), "cloud configuration");
        loaded.Validate();
        Assert.Equal(config.PrimaryCapture, loaded.PrimaryCapture);
    }

    [Theory]
    [InlineData("sftp-home:Media")]
    [InlineData("sftp-home:Media/child")]
    [InlineData("sftp-home:Media/Videos")]
    public void ReplicaCannotOverlapPrimary(string remote)
    {
        var config = Config();
        Assert.Throws<InvalidDataException>((config with { Replication = config.Replication! with { Targets = [new("copy", remote)] } }).Validate);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void PrimaryPreviewCreatesNoMarkerOrLocalStateAndUnmarkedMediaRefusesApply()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cloud = Config();
        var state = new LocalState(fixture.PathFor("state"));
        Assert.Equal(0, SessionCommands.WritePrimaryInitialization(cloud, state, false, new StringWriter()));
        Assert.False(Directory.Exists(state.Root));
        var config = cloud with { Remote = fixture.PathFor("empty-primary") };
        Assert.Equal(0, SessionCommands.WritePrimaryInitialization(config, state, false, new StringWriter()));
        Assert.False(Directory.Exists(config.ServerCapture.Display));
        Directory.CreateDirectory(config.ServerCapture.Display);
        File.WriteAllText(config.ServerCapture.Append("unrelated.mp4").Display, "retain");
        Assert.Throws<UnsafeOperationException>(() => SessionCommands.WritePrimaryInitialization(config, state, true, new StringWriter()));
        Assert.False(File.Exists(config.ServerCapture.Append(MedalSyncConfig.StorageMarker).Display));
        File.Delete(config.ServerCapture.Append("unrelated.mp4").Display);
        Assert.Equal(0, SessionCommands.WritePrimaryInitialization(config, state, true, new StringWriter()));
        Assert.Equal(config.LibraryId, File.ReadAllText(config.ServerCapture.Append(MedalSyncConfig.StorageMarker).Display).Trim());
        Assert.Throws<UnsafeOperationException>(() => SessionCommands.WritePrimaryInitialization(config with { LibraryId = new string('b', 32) }, state, true, new StringWriter()));
    }

    [Fact]
    public void ConfigCreateCanWriteAndReloadACloudPrimary()
    {
        string target = fixture.PathFor("cloud-config.json");
        var output = new StringWriter();
        var error = new StringWriter();
        int code = CommandRunner.Run(["config", "create", "--account-id", MedalFixture.AccountId,
            "--new-library", "--remote", "sftp-home:Media", "--coordination-root", @"\\server\share\Journal",
            "--cache-root", @"E:\Cache", "--local-copy-root", @"E:\Persistent", "--rclone-config", @"C:\private\rclone.conf",
            "--out", target, "--apply"], output, error);
        Assert.True(code == 0, error.ToString());
        Assert.True(MedalSyncConfig.Load(target).PrimaryIsRclone);
    }

    [Fact]
    public void RcloneOutputIsBoundedEvenWithoutNewlines()
    {
        Assert.Throws<RcloneFailedException>(() => new RcloneRunner("pwsh").Run(
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write(('x' * 17000000))"], TimeSpan.FromSeconds(30), captureOutput: true));
    }

    [Fact]
    public void MarkerListingAndMissingMediaUseRemoteCommands()
    {
        var config = Config();
        List<string> commands = [];
        var checker = new RcloneAssetChecker(config, new(fixture.PathFor("state")), args =>
        {
            commands.Add(args[0]);
            Assert.Contains(config.Replication!.RcloneConfigPath!, args);
            return args[0] == "cat" ? config.LibraryId : """[{"Path":"clip.mp4","Size":5,"IsDir":false}]""";
        });
        checker.RequireServer();
        Assert.Equal(["cat"], commands);
        Assert.True(checker.ExistsOnServer(@"M:\Clips\clip.mp4"));
        Assert.False(checker.ExistsOnServer(@"M:\Clips\missing.mp4"));
        Assert.False(checker.ExistsOnServer(@"C:\private\clip.mp4"));
        Assert.Equal(["cat", "lsjson"], commands);
        Assert.Empty(checker.OnServer(Snapshot()).Blocking);
    }

    [Fact]
    public void MissingMarkerAndOfflineAreDifferentRefusals()
    {
        var config = Config();
        var state = new LocalState(fixture.PathFor("state"));
        Assert.Throws<UnsafeOperationException>(() => new RcloneAssetChecker(config, state,
            _ => throw new RcloneFailedException("missing", 4)).RequireServer());
        Assert.Throws<StorageOfflineException>(() => new RcloneAssetChecker(config, state,
            _ => throw new RcloneFailedException("offline", 5)).RequireServer());
        Assert.Throws<UnsafeOperationException>(() => new RcloneAssetChecker(config, state, _ => new string('b', 32)).RequireServer());
    }

    [Theory]
    [InlineData("[{\"Path\":\"../secret.mp4\",\"Size\":1}]")]
    [InlineData("[{\"Path\":\"clip.mp4\",\"Size\":1},{\"Path\":\"CLIP.mp4\",\"Size\":1}]")]
    [InlineData("[{\"Path\":\"clip.mp4\",\"Size\":1},{\"Path\":\"CLIP.mp4\",\"Size\":0,\"IsDir\":true}]")]
    [InlineData("[{\"Path\":\"clip.mp4\",\"Size\":-1}]")]
    [InlineData("[{\"Path\":\"clip.mp4\",\"Size\":1,\"IsLink\":true}]")]
    [InlineData("{}")]
    public void AmbiguousOrUnsafeListingNeverProvesPresence(string listing)
    {
        var config = Config();
        var checker = new RcloneAssetChecker(config, new(fixture.PathFor("state")), args => args[0] == "cat" ? config.LibraryId : listing);
        Assert.Throws<UnsafeOperationException>(() => checker.OnServer(Snapshot()));
    }

    [Fact]
    public void EmptyPrimaryInitializationIsImmutableAndReadBack()
    {
        var config = Config();
        bool marked = false;
        var checker = new RcloneAssetChecker(config, new(fixture.PathFor("state")), args =>
        {
            if (args[0] == "cat") return marked ? config.LibraryId : throw new RcloneFailedException("missing", 4);
            if (args[0] == "copyto")
            {
                Assert.Contains("--immutable", args);
                Assert.Equal(config.LibraryId, File.ReadAllText(args[1]).Trim());
                marked = true;
            }
            return "[]";
        });
        checker.Initialize(false);
        Assert.True(marked);
        checker.Initialize(false);
        Assert.Empty(Directory.GetFiles(fixture.PathFor("state"), "primary-marker-*"));
    }

    [Fact]
    public void UnmarkedNonemptyPrimaryIsNeverAdopted()
    {
        var config = Config();
        var checker = new RcloneAssetChecker(config, new(fixture.PathFor("state")), args => args[0] switch
        {
            "cat" => throw new RcloneFailedException("missing", 4),
            "lsjson" => """[{"Path":"unrelated","Size":0,"IsDir":true}]""",
            _ => throw new Exception("Must not write"),
        });
        Assert.Throws<UnsafeOperationException>(() => checker.Initialize(false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompleteLocalCollectionNeedsNoHydrationEvenWithEmptyOrOfflinePrimary(bool offline)
    {
        var config = Config();
        Claim(config);
        File.WriteAllText(config.MountedCapture.Append("clip.mp4").Display, "retained bytes");
        new StorageReplication(config, new(fixture.PathFor("state")), () => { }, _ => Assert.Fail("Must not download existing media"),
            primary: new Primary(offline, false)).Prepare(Snapshot());
        Assert.Equal("retained bytes", File.ReadAllText(config.MountedCapture.Append("clip.mp4").Display));
    }

    [Fact]
    public void MissingLocalMediaWaitsForOfflinePrimaryAndRefusesIfMissingInBoth()
    {
        var config = Config();
        var snapshot = Snapshot();
        Assert.Throws<StorageOfflineException>(() => new StorageReplication(config, new(fixture.PathFor("state")), () => { },
            _ => Assert.Fail("Must not copy"), primary: new Primary(true)).Prepare(snapshot));
        Assert.Throws<IOException>(() => new StorageReplication(config, new(fixture.PathFor("state")), () => { },
            _ => Assert.Fail("Must not copy"), primary: new Primary(false, false)).Prepare(snapshot));
        Assert.False(Directory.Exists(config.MountedCapture.Display));
    }

    [Fact]
    public void OptionalFailurePersistsAndClearsOnSuccessWhileRequiredFailureBlocks()
    {
        var config = Config();
        config = config with { Replication = config.Replication! with { Targets = [new("backup", "cloud:Backup")] } };
        Claim(config);
        File.WriteAllText(config.MountedCapture.Append("clip.mp4").Display, "synthetic bytes");
        Directory.CreateDirectory(Path.GetDirectoryName(config.Replication.RcloneConfigPath!)!);
        File.WriteAllText(config.Replication.RcloneConfigPath!, "synthetic credentials");
        var state = new LocalState(fixture.PathFor("state"));
        var snapshot = Snapshot();
        bool fail = true;
        var replication = new StorageReplication(config, state, () => { }, args =>
        {
            if (fail && args[2].StartsWith("cloud:")) throw new RcloneFailedException("synthetic outage", 5);
        }, primary: new Primary());
        Assert.Equal(["backup"], replication.Publish(snapshot).Deferred);
        var first = Assert.Single(StorageReplication.Pending(state, config.LibraryId));
        Assert.Single(replication.Publish(snapshot).Deferred);
        Assert.Equal(first.Since, Assert.Single(StorageReplication.Pending(state, config.LibraryId)).Since);
        using (var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(state.Root, "replication", "last-verified.json"))))
            Assert.Equal("backup", receipt.RootElement.GetProperty("deferred")[0].GetString());
        fail = false;
        Assert.Empty(replication.Publish(snapshot).Deferred);
        Assert.Empty(StorageReplication.Pending(state, config.LibraryId));
        Assert.False(File.Exists(Path.Combine(state.Root, "replication", "pending.json")));
        var required = config with { Replication = config.Replication with { Targets = [new("backup", "cloud:Backup") { Required = true }] } };
        Assert.Throws<RcloneFailedException>(() => new StorageReplication(required, state, () => { },
            _ => throw new RcloneFailedException("outage", 5), primary: new Primary()).Publish(snapshot));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationAndIdleRefusalsAreNeverDeferred(bool cancel)
    {
        var config = Config();
        config = config with { Replication = config.Replication! with { Targets = [new("backup", "cloud:Backup")] } };
        Claim(config);
        File.WriteAllText(config.MountedCapture.Append("clip.mp4").Display, "synthetic bytes");
        Directory.CreateDirectory(Path.GetDirectoryName(config.Replication.RcloneConfigPath!)!);
        File.WriteAllText(config.Replication.RcloneConfigPath!, "synthetic");
        var state = new LocalState(fixture.PathFor("state"));
        Assert.ThrowsAny<Exception>(() => new StorageReplication(config, state, () => { }, args =>
        {
            if (args[2].StartsWith("cloud:"))
            {
                if (cancel) throw new OperationCanceledException();
                throw new IOException("Medal reopened");
            }
        }, primary: new Primary()).Publish(Snapshot()));
        Assert.False(File.Exists(Path.Combine(state.Root, "replication", "last-verified.json")));
        Assert.Empty(StorageReplication.Pending(state, config.LibraryId));
    }
}

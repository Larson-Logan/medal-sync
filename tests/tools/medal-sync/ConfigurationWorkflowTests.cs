using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

public sealed class ConfigurationWorkflowTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        return (CommandRunner.Run(args, output, error), output.ToString(), error.ToString());
    }

    private string[] Options(string name = "config.json") => ["config", "create", "--account-id", "12345", "--cache-root", @"E:\Cache",
        "--remote", @"\\server\Data\Media\Videos", "--coordination-root", @"\\server\Data\AppData\MedalSync", "--out", _fixture.PathFor(name)];

    [Fact]
    public void PreviewWritesNothingAndApplyCreatesAValidatedReusableIdentity()
    {
        string path = _fixture.PathFor("config.json");
        Assert.Equal(0, Run([.. Options(), "--new-library"]).Code);
        Assert.False(File.Exists(path));
        var applied = Run([.. Options(), "--new-library", "--apply"]);
        Assert.Equal(0, applied.Code);
        var first = MedalSyncConfig.Load(path);
        Assert.Equal("12345", first.AccountId);
        Assert.NotEqual(new string('0', 32), first.LibraryId);
        Assert.Equal(0, Run("config", "validate", "--config", path).Code);
        byte[] original = File.ReadAllBytes(path);
        Assert.Equal(1, Run([.. Options(), "--new-library", "--apply"]).Code);
        Assert.Equal(original, File.ReadAllBytes(path));

        Assert.Equal(0, Run([.. Options("second.json"), "--library-id", first.LibraryId, "--apply"]).Code);
        var second = MedalSyncConfig.Load(_fixture.PathFor("second.json"));
        Assert.Equal(first.LibraryId, second.LibraryId);
        Assert.False(File.Exists(_fixture.PathFor("device.json")));
    }

    [Theory]
    [InlineData(new string[0], 2)]
    [InlineData(new string[] { "--new-library", "--library-id", "0123456789abcdef0123456789abcdef" }, 2)]
    [InlineData(new string[] { "--new-library", "--account-id", "0" }, 2)]
    [InlineData(new string[] { "--new-library", "--cache-root", @"M:\Cache" }, 1)]
    [InlineData(new string[] { "--new-library", "--remote", @"E:\Videos" }, 1)]
    public void InvalidConfigurationDoesNotWrite(string[] extra, int code)
    {
        Assert.Equal(code, Run([.. Options(), .. extra, "--apply"]).Code);
        Assert.False(File.Exists(_fixture.PathFor("config.json")));
    }

    [Fact]
    public void SpikeRequiresItsOwnRemoteAndControlPort()
    {
        Assert.Equal(2, Run([.. Options(), "--new-library", "--spike", "--apply"]).Code);
        Assert.Equal(0, Run([.. Options(), "--new-library", "--spike", "--remote", @"\\server\Data\AppData\MedalSync-spike", "--apply"]).Code);
        var config = MedalSyncConfig.Load(_fixture.PathFor("config.json"));
        Assert.Equal(53683, config.Mount.ControlPort);
        Assert.Empty(config.SyncExclusions);
    }

    [Fact]
    public void LocalAndCloudOptionsRoundTripWithoutReadingTheProviderOrLibrary()
    {
        var result = Run([.. Options(), "--new-library", "--local-copy-root", @"E:\MedalLibrary",
            "--cloud-remote", "onedrive:MedalSync", "--rclone-config", @"C:\Private\rclone.conf", "--apply"]);
        Assert.True(result.Code == 0, result.Error);
        var config = MedalSyncConfig.Load(_fixture.PathFor("config.json"));
        Assert.Equal(@"E:\MedalLibrary", config.Replication!.LocalCopyRoot);
        Assert.Equal("onedrive:MedalSync", Assert.Single(config.Replication.Targets).Remote);
        Assert.Equal(0, Run("config", "validate", "--config", _fixture.PathFor("config.json")).Code);
        Assert.False(Directory.Exists(_fixture.PathFor("replication")));
    }

    [Fact]
    public void CloudOptionsWithoutCredentialFileAreRefusedBeforeWriting()
    {
        Assert.Equal(1, Run([.. Options(), "--new-library", "--cloud-remote", "onedrive:MedalSync", "--apply"]).Code);
        Assert.False(File.Exists(_fixture.PathFor("config.json")));
    }

    [Fact]
    public void LocalCloudConfigurationNeedsNoSmbServer()
    {
        var result = Run([.. Options(), "--new-library", "--storage-mode", "local", "--remote", @"E:\MedalLibrary",
            "--coordination-root", @"C:\MedalState\coordination", "--cloud-remote", "dropbox:MedalSync",
            "--rclone-config", @"C:\Private\rclone.conf", "--apply"]);
        Assert.True(result.Code == 0, result.Error);
        var config = MedalSyncConfig.Load(_fixture.PathFor("config.json"));
        Assert.Equal(StorageMode.Local, config.StorageMode);
        Assert.Equal(@"E:\MedalLibrary", config.MountRemote);
        Assert.Equal(0, Run("config", "validate", "--config", _fixture.PathFor("config.json")).Code);
    }
}

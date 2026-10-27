using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class WindowsSetupTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private string DirectoryFor(string name)
    {
        string path = _fixture.PathFor(name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string ManagedBin()
    {
        string path = DirectoryFor("program-files/bin");
        File.WriteAllText(Path.Combine(path, "setup-layout.json"), """{"format":1,"layout":"windows-setup"}""");
        return path;
    }

    [Fact]
    public void ProgramFilesInstallUsesWritableUserStateWithoutCreatingIt()
    {
        string profile = DirectoryFor("user");
        string appData = DirectoryFor("appdata");
        string state = LocalState.ResolveRoot(ManagedBin(), profile, appData);
        Assert.Equal(Path.Combine(profile, ".medal-sync"), state);
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void ProgramFilesInstallPreservesConfiguredPhysicalRootAndIdentity()
    {
        string profile = DirectoryFor("user");
        string root = Path.Combine(profile, ".medal-sync");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "config.json"), "synthetic config");
        File.WriteAllText(Path.Combine(root, "device.json"), "synthetic identity");
        Assert.Equal(root, LocalState.ResolveRoot(ManagedBin(), profile, DirectoryFor("appdata")));
        Assert.Equal("synthetic identity", File.ReadAllText(Path.Combine(root, "device.json")));
    }

    [Fact]
    public void ProgramFilesInstallRetainsAnOlderAppDataConfiguration()
    {
        string appData = DirectoryFor("appdata");
        string root = Path.Combine(appData, "LarsonServer", "MedalSync");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "config.json"), "synthetic");
        Assert.Equal(root, LocalState.ResolveRoot(ManagedBin(), DirectoryFor("user"), appData));
    }

    [Fact]
    public void UnmanagedBuildRetainsLegacyIdentityWithoutMovingOrCopyingIt()
    {
        string appData = DirectoryFor("appdata");
        string root = Path.Combine(appData, "LarsonServer", "MedalSync");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "device.json"), "synthetic identity");
        Assert.Equal(root, LocalState.ResolveRoot(DirectoryFor("build"), DirectoryFor("user"), appData));
        Assert.Equal("synthetic identity", File.ReadAllText(Path.Combine(root, "device.json")));
    }

    [Fact]
    public void ManagedInstallationNeverAdoptsConfigurationCopiedIntoProgramFiles()
    {
        string bin = ManagedBin();
        string programs = Path.GetDirectoryName(bin)!;
        File.WriteAllText(Path.Combine(programs, "config.json"), "synthetic");
        File.WriteAllText(Path.Combine(programs, "device.json"), "synthetic");
        string profile = DirectoryFor("user");
        Assert.Equal(Path.Combine(profile, ".medal-sync"), LocalState.ResolveRoot(bin, profile, DirectoryFor("appdata")));
    }

    [Fact]
    public void UnknownLayoutRefusesInsteadOfWritingToProgramFiles()
    {
        string bin = ManagedBin();
        File.WriteAllText(Path.Combine(bin, "setup-layout.json"), """{"format":99,"layout":"unknown"}""");
        Assert.Throws<InvalidDataException>(() => LocalState.ResolveRoot(bin, DirectoryFor("user"), DirectoryFor("appdata")));
    }

    [Fact]
    public void FreshReadinessCheckDoesNotCreateState()
    {
        string root = _fixture.PathFor("absent");
        SetupMaintenance.RequireReady(root, false, false);
        Assert.False(Directory.Exists(root));
        Assert.ThrowsAny<IOException>(() => new LocalState(root).ExistingDevice());
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SetupRefusesAnActiveApplicationOrSupervisor(bool app, bool supervisor)
    {
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(_fixture.PathFor("absent"), app, supervisor));
    }

    [Fact]
    public void RetainedSessionAndPartialConfigurationBlockSetup()
    {
        string root = DirectoryFor("pending");
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        File.WriteAllText(Path.Combine(root, "sessions", "pending.json"), "synthetic");
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(root, false, false));
        string partial = DirectoryFor("partial");
        File.WriteAllText(Path.Combine(partial, "config.json"), "synthetic");
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(partial, false, false));
        Assert.False(File.Exists(Path.Combine(partial, "device.json")));
    }

    [Fact]
    public void AnOpenSetupBlocksLibraryOperationsUntilClosed()
    {
        string name = "MedalSync.Setup.Test." + Guid.NewGuid().ToString("N");
        using (var mutex = new Mutex(false, name))
            Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireNotRunning(name));
        SetupMaintenance.RequireNotRunning(name);
    }

    [Fact]
    public void ExistingLibraryMustBeCleanAndIsNeverChangedByReadinessChecks()
    {
        string root = DirectoryFor("configured");
        var settings = new MedalSyncConfig(1, new string('a', 32), MedalFixture.AccountId,
            @"\\192.0.2.10\Data\Coordination", @"\\192.0.2.10\Data\Videos", @"M:\Clips", @"E:\MedalCache",
            @"C:\Tools\rclone.exe", new MountConfig(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []);
        byte[] config = Json.Serialize(settings);
        File.WriteAllBytes(Path.Combine(root, "config.json"), config);
        var device = new LocalState(root).Device();
        byte[] identity = File.ReadAllBytes(Path.Combine(root, "device.json"));
        var clean = Ownership.Initial(settings.LibraryId, settings.AccountId, DateTimeOffset.UtcNow);
        SetupMaintenance.RequireReady(root, false, false, _ => clean);
        foreach (var state in new[] { OwnershipState.Starting, OwnershipState.Active, OwnershipState.Draining,
            OwnershipState.Publishing, OwnershipState.RecoveryRequired, (OwnershipState)999 })
            Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(root, false, false,
                _ => clean with { State = state, Owner = device, SessionId = new string('1', 32) }));
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(root, false, false,
            _ => clean with { Owner = device }));
        Assert.Throws<IOException>(() => SetupMaintenance.RequireReady(root, false, false, _ => throw new IOException("offline")));
        Assert.Equal(config, File.ReadAllBytes(Path.Combine(root, "config.json")));
        Assert.Equal(identity, File.ReadAllBytes(Path.Combine(root, "device.json")));
    }
}

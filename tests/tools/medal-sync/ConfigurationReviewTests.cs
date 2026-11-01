using MedalSync.Config;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class ConfigurationReviewTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private LocalState State => new(_fixture.PathFor("state"));
    private static MedalSyncConfig Candidate => new(1, "0123456789abcdef0123456789abcdef", "12345", @"D:\Coordination", @"D:\Media",
        @"M:\Clips", @"E:\Cache", @"C:\Programs\rclone.exe", new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []) { StorageMode = StorageMode.Local };
    private static void ValidEnvironment(MedalSyncConfig config, bool fresh) { }
    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreviewAndCancellationLeaveFreshSetupUntouched(bool fresh)
    {
        var review = ConfigurationReview.Prepare(State, Candidate, fresh, ValidEnvironment);
        Assert.Contains(State.ConfigPath, review.Text);
        Assert.Contains(fresh ? "New library" : "Join existing library", review.Text);
        Assert.Contains("minimum free space: 20 GiB", review.Text);
        Assert.Null(review.Save(false, (_, _) => Assert.Fail("Cancellation must not probe storage or write files.")));
        Assert.False(Directory.Exists(State.Root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfirmedFreshOrJoinedSetupWritesOnlyTheReviewedConfiguration(bool fresh)
    {
        int checks = 0;
        var review = ConfigurationReview.Prepare(State, Candidate, fresh, (_, choice) => { Assert.Equal(fresh, choice); checks++; });
        Assert.Null(review.Save(true, (_, choice) => { Assert.Equal(fresh, choice); checks++; }));
        Assert.Equal(2, checks);
        Assert.Equal(Candidate.LibraryId, MedalSyncConfig.Load(State.ConfigPath).LibraryId);
        Assert.Equal(new[] { State.ConfigPath }, Directory.GetFiles(State.Root));
    }

    [Fact]
    public void ExistingConfigurationHasAVerifiedBackupAndRetainsItsDeviceIdentity()
    {
        Directory.CreateDirectory(State.Root);
        byte[] original = Json.Serialize(Candidate);
        File.WriteAllBytes(State.ConfigPath, original);
        string device = Path.Combine(State.Root, "device.json");
        File.WriteAllText(device, "synthetic identity kept byte-for-byte");
        var updated = Candidate with { MaxSessionHours = 6 };
        var review = ConfigurationReview.Prepare(State, updated, false, ValidEnvironment);
        Assert.True(review.ReplacesExisting);
        Assert.Equal(original, File.ReadAllBytes(State.ConfigPath));
        string backup = Assert.IsType<string>(review.Save(true, ValidEnvironment));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(6, MedalSyncConfig.Load(State.ConfigPath).MaxSessionHours);
        Assert.Equal("synthetic identity kept byte-for-byte", File.ReadAllText(device));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedConfigurationOrIdentityRequiresAnotherReview(bool device)
    {
        var review = ConfigurationReview.Prepare(State, Candidate, true, ValidEnvironment);
        Directory.CreateDirectory(State.Root);
        string path = device ? Path.Combine(State.Root, "device.json") : State.ConfigPath;
        File.WriteAllText(path, "concurrently created evidence");
        Assert.Throws<UnsafeOperationException>(() => review.Save(true, ValidEnvironment));
        Assert.Equal("concurrently created evidence", File.ReadAllText(path));
    }

    [Fact]
    public void ChangedStateDuringFinalStorageCheckIsRefused()
    {
        var review = ConfigurationReview.Prepare(State, Candidate, true, ValidEnvironment);
        Assert.Throws<UnsafeOperationException>(() => review.Save(true, (_, _) =>
        {
            Directory.CreateDirectory(State.Root);
            File.WriteAllText(State.ConfigPath, "changed during check");
        }));
        Assert.Equal("changed during check", File.ReadAllText(State.ConfigPath));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("device")]
    [InlineData("sessions")]
    public void ExistingIdentityAndUnfinishedSessionsAreNeverReinitialized(string kind)
    {
        Directory.CreateDirectory(State.Root);
        if (kind == "config") File.WriteAllBytes(State.ConfigPath, Json.Serialize(Candidate));
        if (kind == "device") File.WriteAllText(Path.Combine(State.Root, "device.json"), "retained identity");
        if (kind == "sessions") { Directory.CreateDirectory(Path.Combine(State.Root, "sessions")); File.WriteAllText(Path.Combine(State.Root, "sessions", "retained.json"), "uncertain uploads"); }
        Assert.Throws<UnsafeOperationException>(() => ConfigurationReview.Prepare(State, Candidate, true, ValidEnvironment));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ControlPortCannotChangeWithAnUnfinishedSession(bool activeAtReview)
    {
        Directory.CreateDirectory(State.Root);
        byte[] original = Json.Serialize(Candidate);
        File.WriteAllBytes(State.ConfigPath, original);
        var updated = Candidate with { Mount = Candidate.Mount with { ControlPort = MountConfig.DefaultControlPort } };
        var review = activeAtReview ? null : ConfigurationReview.Prepare(State, updated, false, ValidEnvironment);
        string sessions = Path.Combine(State.Root, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "retained.json"), "unfinished uploads on the original port");

        if (activeAtReview)
            Assert.Throws<UnsafeOperationException>(() => ConfigurationReview.Prepare(State, updated, false, ValidEnvironment));
        else
            Assert.Throws<UnsafeOperationException>(() => review!.Save(true, ValidEnvironment));

        Assert.Equal(original, File.ReadAllBytes(State.ConfigPath));
        Assert.False(Directory.Exists(State.BackupDirectory));
    }

    [Fact]
    public void InvalidPathsAndCompatibilityOrStorageFailuresWriteNothing()
    {
        Assert.Throws<InvalidDataException>(() => ConfigurationReview.Prepare(State, Candidate with { CacheRoot = @"M:\Cache" }, true, ValidEnvironment));
        Assert.Throws<IncompatibleLibraryException>(() => ConfigurationReview.Prepare(State, Candidate, true, (_, _) => throw new IncompatibleLibraryException("Synthetic schema refusal")));
        var review = ConfigurationReview.Prepare(State, Candidate, true, ValidEnvironment);
        Assert.Throws<IOException>(() => review.Save(true, (_, _) => throw new IOException("Synthetic storage disconnected")));
        Assert.False(Directory.Exists(State.Root));
    }

    [Fact]
    public void ACleanCandidateCannotBypassTheOriginalLibraryOwnership()
    {
        Directory.CreateDirectory(State.Root);
        File.WriteAllBytes(State.ConfigPath, Json.Serialize(Candidate));
        var moved = Candidate with { CoordinationRoot = @"D:\DifferentCoordination" };
        Assert.Throws<UnsafeOperationException>(() => ConfigurationReview.Prepare(State, moved, false, (config, _) =>
        {
            if (config.CoordinationRoot == Candidate.CoordinationRoot) throw new UnsafeOperationException("Original journal is active");
        }));
        Assert.Equal(Candidate.CoordinationRoot, MedalSyncConfig.Load(State.ConfigPath).CoordinationRoot);
    }

    [Fact]
    public void OriginalOwnershipIsCheckedAgainImmediatelyBeforeSave()
    {
        Directory.CreateDirectory(State.Root);
        byte[] original = Json.Serialize(Candidate);
        File.WriteAllBytes(State.ConfigPath, original);
        var review = ConfigurationReview.Prepare(State, Candidate with { CoordinationRoot = @"D:\DifferentCoordination" }, false, ValidEnvironment);
        Assert.Throws<UnsafeOperationException>(() => review.Save(true, (config, _) =>
        {
            if (config.CoordinationRoot == Candidate.CoordinationRoot) throw new UnsafeOperationException("Original journal became active");
        }));
        Assert.Equal(original, File.ReadAllBytes(State.ConfigPath));
        Assert.False(Directory.Exists(State.BackupDirectory));
    }
}

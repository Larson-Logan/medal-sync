using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

/// <summary>Outages wait calmly; sign-in, configuration, conflict, quota and verification failures name their cause.</summary>
public sealed class StorageFailureTests : IDisposable
{
    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static MedalSyncConfig CloudPrimary() => new(1, new string('a', 32), MedalFixture.AccountId,
        @"\\server\share\Journal", "onedrive:MedalSync", @"M:\Clips", @"E:\Cache", @"C:\Tools\rclone.exe",
        new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, [])
    {
        Replication = new(@"E:\MedalLibrary", @"C:\Private\rclone.conf", []),
    };

    private RcloneAssetChecker Checker(Func<IReadOnlyList<string>, string>? run) =>
        new(CloudPrimary(), new LocalState(_fixture.PathFor("state")), run);

    [Theory]
    [InlineData("Failed to create file system: couldn't fetch token - maybe it has expired? - refresh with \"rclone config reconnect onedrive:\"", (int)RcloneHints.SignIn)]
    [InlineData("error: invalid_grant: AADSTS70000 the provided grant has expired", (int)RcloneHints.SignIn)]
    [InlineData("Failed to create file system for \"backup:x\": didn't find section in config file", (int)RcloneHints.Config)]
    [InlineData("Couldn't decrypt configuration, most likely wrong password", (int)RcloneHints.Config)]
    [InlineData("Failed to copy: quotaLimitReached: Insufficient Space Available", (int)RcloneHints.Quota)]
    [InlineData("Source and destination exist but do not match: immutable file modified", (int)RcloneHints.Conflict)]
    [InlineData("Post \"https://login.example/token\": dial tcp: lookup login.example: no such host", (int)RcloneHints.Network)]
    [InlineData("couldn't fetch token: dial tcp 192.0.2.1:443: i/o timeout", (int)(RcloneHints.Network | RcloneHints.SignIn))]
    [InlineData("2 files to upload", (int)RcloneHints.None)]
    public void ErrorOutputIsReducedToCategories(string stderr, int expected) =>
        Assert.Equal((RcloneHints)expected, RcloneRunner.Classify(stderr.ToLowerInvariant()));

    [Theory]
    [InlineData(5, 0, false, null, true)]
    [InlineData(1, 0, false, "lsjson", true)]
    [InlineData(null, 0, true, "copy", true)]
    [InlineData(7, (int)RcloneHints.Network, false, null, true)]
    [InlineData(1, (int)RcloneHints.SignIn, false, null, false)]
    [InlineData(1, (int)RcloneHints.Config, false, null, false)]
    [InlineData(1, 0, false, "check", false)]
    [InlineData(2, 0, false, null, false)]
    [InlineData(3, 0, false, null, false)]
    [InlineData(6, (int)RcloneHints.Conflict, false, null, false)]
    [InlineData(7, 0, false, null, false)]
    [InlineData(null, 0, false, null, false)] // rclone could not start, or the credential file is missing
    public void OnlyOutagesAndTimeoutsAreTransient(int? exitCode, int hints, bool timedOut, string? command, bool transient) =>
        Assert.Equal(transient, RcloneFailure.IsTransient(new RcloneFailedException("synthetic", exitCode, hints: (RcloneHints)hints, timedOut: timedOut, command: command)));

    [Theory]
    [InlineData("onedrive:MedalSync/Clips", "onedrive")]
    [InlineData("my drive:Medal", "my drive")]
    [InlineData(@"E:\MedalLibrary", null)]
    [InlineData(@"\\server\share\Media", null)]
    public void RemoteNameComesFromRclonePathsOnly(string destination, string? remote) =>
        Assert.Equal(remote, RcloneFailure.Remote(destination));

    [Theory]
    [InlineData(5, (int)RcloneHints.None)]
    [InlineData(1, (int)RcloneHints.None)]
    [InlineData(7, (int)RcloneHints.Network)]
    public void PrimaryOutageIsAWait(int exitCode, int hints)
    {
        var checker = Checker(_ => throw new RcloneFailedException("synthetic", exitCode, hints: (RcloneHints)hints, command: "cat"));
        Assert.Throws<StorageOfflineException>(checker.RequireServer);
    }

    [Fact]
    public void PrimaryTimeoutIsAWait()
    {
        var checker = Checker(_ => throw new RcloneFailedException("synthetic timeout", timedOut: true, command: "cat"));
        Assert.Throws<StorageOfflineException>(checker.RequireServer);
    }

    [Theory]
    [InlineData(1, (int)RcloneHints.SignIn, "rclone config reconnect \"onedrive:\" --config \"C:\\Private\\rclone.conf\"")]
    [InlineData(1, (int)RcloneHints.Config, "opens without a password prompt")]
    [InlineData(7, (int)RcloneHints.None, "failed (exit 7)")]
    [InlineData(6, (int)RcloneHints.None, "failed (exit 6)")]
    [InlineData(2, (int)RcloneHints.None, "refused its command line")]
    public void PrimarySignInConfigurationAndFatalErrorsNameTheCause(int exitCode, int hints, string expected)
    {
        var checker = Checker(_ => throw new RcloneFailedException("synthetic", exitCode, hints: (RcloneHints)hints, command: "cat"));
        var refused = Assert.Throws<StorageActionRequiredException>(checker.RequireServer);
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Primary storage onedrive:MedalSync", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListingFailuresAreClassifiedTheSameWay()
    {
        var checker = Checker(arguments => arguments[0] == "cat" ? new string('a', 32)
            : throw new RcloneFailedException("synthetic", 1, hints: RcloneHints.SignIn, command: "lsjson"));
        checker.RequireServer();
        Assert.Throws<StorageActionRequiredException>(() => checker.ExistsOnServer(@"M:\Clips\one.mp4"));
    }

    [Fact]
    public void MissingPrimaryCredentialFileNamesItsPathInsteadOfWaiting()
    {
        // No stub: the credential check runs before rclone would start.
        var refused = Assert.Throws<StorageActionRequiredException>(Checker(run: null).RequireServer);
        Assert.Contains(@"is missing at C:\Private\rclone.conf", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RcloneRunnerKeepsOnlyErrorCategoriesAndMarksTimeouts()
    {
        var signIn = Assert.Throws<RcloneFailedException>(() => new RcloneRunner("pwsh").Run(
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.WriteLine('Failed: invalid_grant token secret-value'); exit 1"], TimeSpan.FromSeconds(30)));
        Assert.Equal(1, signIn.ExitCode);
        Assert.Equal(RcloneHints.SignIn, signIn.Hints);
        Assert.DoesNotContain("secret-value", signIn.Message, StringComparison.Ordinal);
        var timeout = Assert.Throws<RcloneFailedException>(() => new RcloneRunner("pwsh").Run(
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], TimeSpan.FromSeconds(1)));
        Assert.True(timeout.TimedOut);
        Assert.True(RcloneFailure.IsTransient(timeout));
    }

    // Replication runs against real (synthetic) folders, so these use the Windows paths of the fixture.

    private sealed class Primary(Exception? failure = null) : IAssetChecker
    {
        public void RequireServer() { if (failure is not null) throw failure; }
        public bool ExistsOnServer(string path) => true;
        public AssetReport ThroughMount(LibrarySnapshot snapshot) => throw new NotSupportedException();
        public AssetReport OnServer(LibrarySnapshot snapshot) => throw new NotSupportedException();
    }

    private (MedalSyncConfig Config, LocalState State, LibrarySnapshot Snapshot) Library(params ReplicaTarget[] targets)
    {
        var config = CloudPrimary() with
        {
            Replication = new(_fixture.PathFor("local"), _fixture.PathFor("private", "rclone.conf"), targets),
        };
        Directory.CreateDirectory(config.MountedCapture.Display);
        File.WriteAllText(config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display, config.LibraryId);
        File.WriteAllText(config.MountedCapture.Append("clip.mp4").Display, "synthetic bytes");
        Directory.CreateDirectory(Path.GetDirectoryName(config.Replication.RcloneConfigPath!)!);
        File.WriteAllText(config.Replication.RcloneConfigPath!, "synthetic credentials");
        string db = _fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "clip", video: @"M:\Clips\clip.mp4");
        return (config, new LocalState(_fixture.PathFor("state")), LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId));
    }

    [Theory]
    [InlineData("copy", 6, (int)RcloneHints.Conflict, "STOP conflict policy")]
    [InlineData("copy", 7, (int)RcloneHints.Quota, "out of space or over its quota")]
    [InlineData("check", 1, (int)RcloneHints.None, "Verification found files at Primary storage onedrive:MedalSync")]
    public void DefinitePrimaryTransferFailuresNeedAction(string failing, int exitCode, int hints, string expected)
    {
        var (config, state, snapshot) = Library();
        var replication = new StorageReplication(config, state, () => { }, arguments =>
        {
            if (arguments[0] == failing) throw new RcloneFailedException("synthetic", exitCode, hints: (RcloneHints)hints, command: failing);
        }, primary: new Primary());
        var refused = Assert.Throws<StorageActionRequiredException>(() => replication.Publish(snapshot));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(state.Root, "replication", "last-verified.json")));
    }

    [Fact]
    public void InterruptedPrimaryTransferIsAWait()
    {
        var (config, state, snapshot) = Library();
        var replication = new StorageReplication(config, state, () => { },
            _ => throw new RcloneFailedException("synthetic", 5, command: "copy"), primary: new Primary());
        Assert.Throws<StorageOfflineException>(() => replication.Publish(snapshot));
    }

    [Fact]
    public void RequiredReplicaSignInFailureNamesTheTargetAndItsRemote()
    {
        var (config, state, snapshot) = Library(new ReplicaTarget("backup", "b2:MedalBackup") { Required = true });
        var replication = new StorageReplication(config, state, () => { }, arguments =>
        {
            if (arguments[2].StartsWith("b2:", StringComparison.Ordinal))
                throw new RcloneFailedException("synthetic", 1, hints: RcloneHints.SignIn, command: arguments[0]);
        }, primary: new Primary());
        var refused = Assert.Throws<StorageActionRequiredException>(() => replication.Publish(snapshot));
        Assert.StartsWith("Required cloud copy 'backup'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("rclone config reconnect \"b2:\"", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchWithBadPrimaryCredentialsRecordsLocallyWithAWarningOnlyWhenNothingMustDownload()
    {
        var (config, state, snapshot) = Library();
        var replication = new StorageReplication(config, state, () => { }, _ => Assert.Fail("Nothing to download"),
            primary: new Primary(new StorageActionRequiredException("Primary storage onedrive:MedalSync did not accept rclone's sign-in.")));
        string? warning = replication.Prepare(snapshot);
        Assert.NotNull(warning);
        Assert.Contains("cannot finish until this is fixed: Primary storage onedrive:MedalSync did not accept", warning, StringComparison.Ordinal);
        File.Delete(config.MountedCapture.Append("clip.mp4").Display);
        var refused = Assert.Throws<StorageActionRequiredException>(() => replication.Prepare(snapshot));
        Assert.StartsWith("1 clip file(s) must be downloaded from primary storage first.", refused.Message, StringComparison.Ordinal);
    }
}

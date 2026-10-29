using System.Runtime.Versioning;
using System.Text.Json;
using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

/// <summary>Argument parsing, input validation, status reachability, abandon previews and clip drive wording.</summary>
public sealed class CliInputAndStatusTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceIdentity First = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "First PC");
    private static readonly DeviceIdentity Second = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Second PC");
    private const string LibraryId = "0123456789abcdef0123456789abcdef";
    private const string SessionA = "11111111111111111111111111111111";

    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = CommandRunner.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    // Repeated options and options used as values are usage errors that name the option.
    [Theory]
    [InlineData("--bitrate", "capacity", "--bitrate", "8", "--bitrate", "9", "--hours", "1", "--free-gb", "100")]
    [InlineData("--help", "--help", "--help")]
    [InlineData("--apply", "config", "create", "--account-id", "1", "--cache-root", @"E:\Cache", "--remote", @"\\a\x", "--out", "c.json", "--new-library", "--apply", "--apply")]
    [InlineData("--remote", "config", "create", "--account-id", "1", "--cache-root", @"E:\Cache", "--remote", @"\\a\x", "--remote", @"\\b\y", "--out", "c.json", "--new-library")]
    [InlineData("--reason", "abandon", "--session", SessionA, "--reason", "--apply")]
    [InlineData("--out", "config", "create", "--account-id", "1", "--cache-root", @"E:\Cache", "--remote", @"\\a\x", "--new-library", "--out", "--apply")]
    [InlineData("--out", "snapshot", "export", "--out", "--db", "x.db")]
    [InlineData("--session", "recover-start", "--session", "--unknown-thing")]
    public void Parser_RejectsRepeatedOptionsAndOptionsTakenAsValues(string named, params string[] args)
    {
        var result = Run(args);
        Assert.Equal(2, result.Code);
        Assert.Contains(named, result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("--bitrate", "0", "--hours", "1", "--free-gb", "100")]
    [InlineData("--bitrate", "-8", "--hours", "1", "--free-gb", "100")]
    [InlineData("--bitrate", "NaN", "--hours", "1", "--free-gb", "100")]
    [InlineData("--bitrate", "Infinity", "--hours", "1", "--free-gb", "100")]
    [InlineData("--bitrate", "1e300", "--hours", "1", "--free-gb", "100")]
    [InlineData("--bitrate", "8", "--hours", "0", "--free-gb", "100")]
    [InlineData("--bitrate", "8", "--hours", "-1", "--free-gb", "100")]
    [InlineData("--bitrate", "8", "--hours", "NaN", "--free-gb", "100")]
    [InlineData("--bitrate", "8", "--hours", "-Infinity", "--free-gb", "100")]
    [InlineData("--bitrate", "8", "--hours", "25", "--free-gb", "100")]
    [InlineData("--bitrate", "8", "--hours", "1e-300", "--free-gb", "100")]
    [InlineData("--bitrate", "8", "--hours", "1", "--free-gb", "-1")]
    [InlineData("--bitrate", "8", "--hours", "1", "--free-gb", "NaN")]
    [InlineData("--bitrate", "8", "--hours", "1", "--free-gb", "Infinity")]
    [InlineData("--bitrate", "8", "--hours", "1", "--free-gb", "1e300")]
    [InlineData("--bitrate", "8", "--hours", "1", "--free-gb", "100", "--reserve-gb", "-1")]
    [InlineData("--bitrate", "8", "--hours", "1", "--free-gb", "100", "--reserve-gb", "NaN")]
    [InlineData("--bitrate", "8", "--hours", "1", "--free-gb", "100", "--reserve-gb", "1e300")]
    [InlineData("--bitrate", "eight", "--hours", "1", "--free-gb", "100")]
    public void Capacity_RejectsOutOfRangeNumbersAsUsageErrors(params string[] options)
    {
        var result = Run(["capacity", .. options]);
        Assert.Equal(2, result.Code);
        Assert.StartsWith("medal-sync: --", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Output);
    }

    [Fact]
    public void Capacity_KeepsExitOneForAValidButInsufficientCalculation()
    {
        Assert.Equal(0, Run("capacity", "--bitrate", "10000", "--hours", "24", "--free-gb", "1000000").Code);
        var empty = Run("capacity", "--bitrate", "8", "--hours", "1", "--free-gb", "0", "--reserve-gb", "0");
        Assert.Equal(1, empty.Code);
        Assert.Contains("short", empty.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789abcdef0123456789abcdef\n", false)]
    [InlineData("0123456789abcdef0123456789abcdef\r\n", false)]
    [InlineData(" 0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef ", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("", false)]
    public void Ids_MatchTheWholeString(string value, bool valid) => Assert.Equal(valid, CoordinationStore.IsValidId(value));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [SupportedOSPlatform("windows")]
    public void RecoverStart_RejectsAnIdWithATrailingNewlineAsUsage(string suffix)
    {
        string[] args = ["recover-start", "--session", SessionA + suffix];
        var result = Run(args);
        Assert.Equal(2, result.Code);
        if (!OperatingSystem.IsWindows()) return;
        Assert.Contains("--session must be 32 lowercase hex", result.Error, StringComparison.Ordinal);
        var parsed = Arguments.Parse(args, new HashSet<string> { "--apply" }, new HashSet<string> { "--session" });
        var refusal = Assert.Throws<UsageException>(() => SessionCommands.RecoverStart(parsed, new StringWriter()));
        Assert.Contains("--session must be 32 lowercase hex", refusal.Message, StringComparison.Ordinal);
    }

    private MedalSyncConfig StatusConfig(string coordinationRoot, string libraryId = LibraryId) =>
        new(1, libraryId, MedalFixture.AccountId, coordinationRoot, _fixture.PathFor("media"), @"M:\Clips", _fixture.PathFor("cache"),
            @"C:\Tools\rclone.exe", new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []) { StorageMode = StorageMode.Local };

    private CoordinationStore NewStore(string root)
    {
        string db = _fixture.CreateLibrary("pc-" + Guid.NewGuid().ToString("N"));
        return CoordinationStore.Create(root, LibraryId, LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId),
            new(AssetManifest.CurrentFormat, [], []), First, SessionA, new("2643.583.1", "2643.3040.1"), Now);
    }

    [SupportedOSPlatform("windows")]
    private static (int Code, string Output) Status(MedalSyncConfig config, LocalState state, bool json)
    {
        var output = new StringWriter();
        int code = SessionCommands.WriteStatus(config, state, First, json, output);
        return (code, output.ToString());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Status_WaitsInTextAndJsonWhenStorageCannotBeReached()
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = new LocalState(_fixture.PathFor("state"));
        var offline = StatusConfig(_fixture.PathFor("offline"));

        var text = Status(offline, state, json: false);
        Assert.Equal(3, text.Code);
        Assert.StartsWith("Waiting for storage", text.Output, StringComparison.Ordinal);

        var json = Status(offline, state, json: true);
        Assert.Equal(3, json.Code);
        using (var document = JsonDocument.Parse(json.Output))
        {
            Assert.Equal("unreachable", document.RootElement.GetProperty("storage").GetString());
            Assert.StartsWith("Waiting for storage", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        var config = StatusConfig(_fixture.PathFor("coordination"));
        var store = NewStore(config.LibraryCoordinationDirectory);
        Assert.Equal(0, Status(config, state, json: true).Code);
        Assert.Equal(0, Status(config, state, json: false).Code);

        // A marker for a different library is a refusal (exit 1), not an outage.
        var other = config with { LibraryId = new string('f', 32) };
        Directory.CreateDirectory(other.LibraryCoordinationDirectory);
        File.Copy(Path.Combine(store.Root, "library.json"), Path.Combine(other.LibraryCoordinationDirectory, "library.json"));
        Assert.Throws<IncompatibleLibraryException>(() => Status(other, state, json: true));
        Assert.Throws<IncompatibleLibraryException>(() => Status(other, state, json: false));

        // A journal that cannot be read is an outage, not a refusal.
        File.Delete(Path.Combine(store.Root, "journal.json"));
        Assert.Equal(3, Status(config, state, json: false).Code);
        Assert.Equal(3, Status(config, state, json: true).Code);
    }

    [SupportedOSPlatform("windows")]
    private static (int Code, string Output, List<string> Log) Abandon(CoordinationStore store, string session, string reason, bool apply)
    {
        var output = new StringWriter();
        var log = new List<string>();
        int code = SessionCommands.WriteAbandon(store, Second, session, reason, apply, output, log.Add);
        return (code, output.ToString(), log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void Abandon_PreviewRefusesWheneverApplyWould(bool apply)
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = NewStore(_fixture.PathFor("server", LibraryId));
        var ready = store.ReadJournal();
        var nothing = Assert.Throws<OwnershipConflictException>(() => Abandon(store, SessionA, "PC lost", apply));
        Assert.Contains("no unfinished session", nothing.Message, StringComparison.Ordinal);
        Assert.Equal(ready, store.ReadJournal());

        var active = store.Update(r => Ownership.Acquire(r, First, SessionA, Now), TimeSpan.FromSeconds(2));
        var mistyped = Assert.Throws<OwnershipConflictException>(() => Abandon(store, new string('2', 32), "PC lost", apply));
        Assert.Contains("does not match", mistyped.Message, StringComparison.Ordinal);
        var blank = Assert.Throws<OwnershipConflictException>(() => Abandon(store, SessionA, " ", apply));
        Assert.Contains("reason", blank.Message, StringComparison.Ordinal);
        Assert.Equal(active, store.ReadJournal());

        var result = Abandon(store, SessionA, "PC lost", apply);
        Assert.Equal(0, result.Code);
        Assert.Contains("Abandoning returns the library", result.Output, StringComparison.Ordinal);
        if (apply)
        {
            Assert.Equal(OwnershipState.Clean, store.ReadJournal().State);
            Assert.Contains(SessionA, Assert.Single(result.Log), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Preview only", result.Output, StringComparison.Ordinal);
            Assert.Equal(active, store.ReadJournal());
            Assert.Empty(result.Log);
        }
    }

    private static MedalSyncConfig DriveConfig(string remote, string? localCopy, StorageMode mode = StorageMode.Server) =>
        new(1, LibraryId, "12345", @"\\192.0.2.10\Data\AppData\MedalSync", remote, @"M:\Clips", @"E:\MedalCache",
            @"C:\Tools\rclone.exe", new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, [])
        {
            StorageMode = mode,
            Replication = localCopy is null ? null : new(localCopy, @"C:\Private\rclone.conf", []),
        };

    [Fact]
    public void ClipDrive_HealthMessagesNameTheFolderTheDriveServes()
    {
        var server = DriveConfig(@"\\192.0.2.10\Data\Media\Videos", null);
        Assert.Equal(@"M:\ serves \\192.0.2.10\Data\Media\Videos.", ClipDriveText.Serving(server));
        Assert.Equal(@"M:\ is not serving \\192.0.2.10\Data\Media\Videos.", ClipDriveText.NotServing(server));

        var localCopy = DriveConfig(@"\\192.0.2.10\Data\Media\Videos", @"E:\MedalLibrary");
        Assert.Equal(@"M:\ serves E:\MedalLibrary (the local copy of primary storage \\192.0.2.10\Data\Media\Videos).", ClipDriveText.Serving(localCopy));
        Assert.StartsWith(@"M:\ is not serving E:\MedalLibrary ", ClipDriveText.NotServing(localCopy), StringComparison.Ordinal);

        var rclonePrimary = DriveConfig("onedrive:MedalSync", @"E:\MedalLibrary");
        Assert.Equal(@"M:\ serves E:\MedalLibrary (the local copy of primary storage onedrive:MedalSync).", ClipDriveText.Serving(rclonePrimary));
        Assert.DoesNotContain("not serving onedrive", ClipDriveText.NotServing(rclonePrimary), StringComparison.Ordinal);

        var local = DriveConfig(@"E:\MedalLibrary", null, StorageMode.Local);
        Assert.Equal(@"M:\ serves E:\MedalLibrary.", ClipDriveText.Serving(local));
    }
}

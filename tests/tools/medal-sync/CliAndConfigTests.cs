using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class CliAndConfigTests : IDisposable
{
    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = CommandRunner.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string Library()
    {
        string db = _fixture.CreateLibrary("pc");
        MedalFixture.AddContent(db, "clip-1", video: @"M:\Clips\Clips\a.mp4", metadata: "{\"title\":\"A\",\"isFavorited\":1}");
        MedalFixture.AddContent(db, "clip-2", video: @"M:\Clips\Clips\b.mp4");
        return db;
    }

    [Fact]
    public void Snapshot_ExportVerifyAndDryRunAgainstAFixture()
    {
        string db = Library();
        string file = _fixture.PathFor("snapshot.json");

        var export = Run("snapshot", "export", "--out", file, "--db", db);
        Assert.Equal(0, export.Code);
        Assert.Contains("2 contents", export.Output, StringComparison.Ordinal);
        Assert.Equal(1, Run("snapshot", "export", "--out", file, "--db", db).Code);

        var verify = Run("snapshot", "verify", "--in", file);
        Assert.Equal(0, verify.Code);
        Assert.Contains("supported", verify.Output, StringComparison.Ordinal);

        string before = MedalFixture.Settings(db);
        var dry = Run("dry-run-restore", "--in", file, "--db", db);
        Assert.True(dry.Code == 0, dry.Output + dry.Error);
        Assert.Contains("read-back identical", dry.Output, StringComparison.Ordinal);
        Assert.Contains("device settings unchanged", dry.Output, StringComparison.Ordinal);
        Assert.Equal(before, MedalFixture.Settings(db));
    }

    [Fact]
    public void Audit_ExitsNonZeroWhenClipsWereDropped()
    {
        string db = Library();
        string checkpoint = _fixture.PathFor("checkpoint.json");
        Assert.Equal(0, Run("snapshot", "export", "--out", checkpoint, "--db", db).Code);
        Assert.Equal(0, Run("audit", "--checkpoint", checkpoint, "--db", db).Code);
        using (var connection = MedalFixture.Open(db))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET video_path = NULL WHERE local_content_id = 'clip-2'");
        }

        var audit = Run("audit", "--checkpoint", checkpoint, "--db", db, "--json");

        Assert.Equal(1, audit.Code);
        Assert.Contains("PATH_CLEARED", audit.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_PutsBackAClearedPathWhoseFileExists()
    {
        string db = _fixture.CreateLibrary("pc");
        string clip = _fixture.PathFor("capture", "kept.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
        File.WriteAllBytes(clip, [1, 2, 3]);
        MedalFixture.AddContent(db, "kept", video: clip, metadata: "{\"title\":\"k\",\"isFavorited\":1}");
        string checkpoint = _fixture.PathFor("checkpoint.json");
        Assert.Equal(0, Run("snapshot", "export", "--out", checkpoint, "--db", db).Code);
        using (var connection = MedalFixture.Open(db))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET video_path = NULL");
        }

        var preview = Run("repair", "--checkpoint", checkpoint, "--db", db);
        Assert.Equal(0, preview.Code);
        Assert.Contains("1 repair(s) possible", preview.Output, StringComparison.Ordinal);
        Assert.Equal(1, Run("audit", "--checkpoint", checkpoint, "--db", db).Code);

        var applied = Run("repair", "--checkpoint", checkpoint, "--db", db, "--apply");
        Assert.True(applied.Code == 0, applied.Output + applied.Error);
        Assert.Equal(0, Run("audit", "--checkpoint", checkpoint, "--db", db).Code);
        Assert.Equal(1, MedalFixture.Count(db, "favorites"));
    }

    [Fact]
    public void LibraryWritersShareTheSupervisorsInstanceLock()
    {
        if (!OperatingSystem.IsWindows()) return;
        string mutexName = @"Local\MedalSync-Test-" + Guid.NewGuid().ToString("N");
        using (SessionCommands.SingleInstance(mutexName))
        {
            Exception? refusal = null;
            var contender = new Thread(() =>
            {
                if (!OperatingSystem.IsWindows()) return;
                try { using var competing = SessionCommands.SingleInstance(mutexName); }
                catch (Exception ex) { refusal = ex; }
            });
            contender.Start();
            Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
            Assert.Contains("already running", Assert.IsType<UnsafeOperationException>(refusal).Message, StringComparison.Ordinal);
        }
        using var recovered = SessionCommands.SingleInstance(mutexName);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("version")]
    [InlineData("fingerprint")]
    [InlineData("columns")]
    [InlineData("definitions")]
    public void AuditAndRepairRefuseUnrelatedCheckpointsBeforePlanning(string mismatch)
    {
        string db = _fixture.CreateLibrary("pc");
        string clip = _fixture.PathFor("existing.mp4");
        File.WriteAllBytes(clip, [1, 2, 3]);
        MedalFixture.AddContent(db, "same-id", video: clip);
        var snapshot = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        var tables = snapshot.Tables.Select(t => mismatch switch
        {
            "columns" when t.Name == "contents" => t with
            {
                Columns = t.Columns.Select(c => c == "video_path" ? "other_path" : c).ToArray(),
            },
            "definitions" when t.Name == "content_counts" => t with
            {
                Rows = t.Rows.Select(r => TableSnapshot.Text(r[t.ColumnIndex("key")]) == MedalSchema.DefinitionsVersionKey
                    ? (IReadOnlyList<Cell?>)r.Select((cell, i) => i == t.ColumnIndex("count") ? new IntegerCell(999) : cell).ToArray()
                    : r).ToArray(),
            },
            _ => t,
        });
        var checkpoint = LibrarySnapshot.Create(mismatch == "account" ? "67890" : snapshot.AccountId,
            snapshot.SchemaVersion + (mismatch == "version" ? 1 : 0),
            mismatch == "fingerprint" ? new string('0', 64) : snapshot.SchemaFingerprint, tables);
        string file = _fixture.PathFor("checkpoint.json");
        File.WriteAllBytes(file, checkpoint.ToCanonicalJson());
        using (var connection = MedalFixture.Open(db)) MedalFixture.Execute(connection, "UPDATE contents SET video_path = NULL");
        string before = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId).Digest();
        string settings = MedalFixture.Settings(db);

        foreach (string[] command in new[] { new[] { "audit" }, new[] { "repair" }, new[] { "repair", "--apply" } })
        {
            var result = Run(command.Concat(["--checkpoint", file, "--db", db]).ToArray());
            Assert.Equal(1, result.Code);
            Assert.Contains(mismatch == "account" ? "account" : mismatch == "columns" ? "columns" : mismatch == "definitions" ? "counter definitions" : "schema",
                result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.Output);
            Assert.Equal(before, LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId).Digest());
            Assert.Equal(settings, MedalFixture.Settings(db));
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(db)!, "medal-sync-backups")));
        }
    }

    [Theory]
    [InlineData(new[] { "snapshot", "export" }, 2)]
    [InlineData(new[] { "snapshot", "export", "--out" }, 2)]
    [InlineData(new[] { "audit", "--checkpoint", "x", "--bogus" }, 2)]
    [InlineData(new[] { "version", "--json" }, 2)]
    [InlineData(new[] { "nonsense" }, 2)]
    [InlineData(new string[0], 2)]
    [InlineData(new[] { "--help" }, 0)]
    [InlineData(new[] { "version" }, 0)]
    public void Cli_RejectsMistakesAndExplainsUsage(string[] args, int expected) => Assert.Equal(expected, Run(args).Code);

    [Fact]
    public void Cli_ReportsRefusalsWithoutStackTraces()
    {
        var missing = Run("snapshot", "verify", "--in", _fixture.PathFor("absent.json"));
        Assert.Equal(1, missing.Code);
        Assert.StartsWith("medal-sync:", missing.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Capacity_CommandReportsTheVerdict()
    {
        Assert.Equal(0, Run("capacity", "--bitrate", "15", "--hours", "4", "--free-gb", "273").Code);
        var tight = Run("capacity", "--bitrate", "100", "--hours", "8", "--free-gb", "200");
        Assert.Equal(1, tight.Code);
        Assert.Contains("short", tight.Output, StringComparison.Ordinal);
    }

    private static MedalSyncConfig Config() => new(
        1, "0123456789abcdef0123456789abcdef", "12345",
        @"\\192.0.2.10\Data\AppData\MedalSync", @"\\192.0.2.10\Data\Media\Videos", @"M:\Clips", @"E:\MedalCache",
        @"%LOCALAPPDATA%\LarsonServer\MedalSync\bin\rclone.exe",
        new MountConfig(5, 20, 20, 300, 30, 1000, 2, 53682), 4, [new SyncExclusion("larson-drive-media", "/Videos/Clips")]);

    [Fact]
    public void Config_DerivesTheServerPathsAndValidates()
    {
        var config = Config();
        config.Validate();
        Assert.Equal(@"\\192.0.2.10\Data\Media\Videos\Clips", config.ServerCapture.Display);
        Assert.Equal(@"\\192.0.2.10\Data\AppData\MedalSync\0123456789abcdef0123456789abcdef", config.LibraryCoordinationDirectory);
        Assert.Equal(@"E:\MedalCache\0123456789abcdef0123456789abcdef\" + new string('1', 32), config.SessionCacheDirectory(new string('1', 32)));
        Assert.Equal('M', config.MountSettings(new string('1', 32), @"C:\log.txt").DriveLetter);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("local-remote")]
    [InlineData("cache-on-mount")]
    [InlineData("library-id")]
    [InlineData("account")]
    [InlineData("hours")]
    public void Config_RefusesUnsafeValues(string change)
    {
        var bad = change switch
        {
            "root" => Config() with { CaptureFolder = @"M:\" },
            "local-remote" => Config() with { Remote = @"D:\Clips" },
            "cache-on-mount" => Config() with { CacheRoot = @"M:\cache" },
            "library-id" => Config() with { LibraryId = "not-an-id" },
            "account" => Config() with { AccountId = "abc" },
            _ => Config() with { MaxSessionHours = 0 },
        };
        Assert.Throws<InvalidDataException>(bad.Validate);
    }

    [Fact]
    public void Config_RoundTripsThroughItsJsonFile()
    {
        string path = _fixture.PathFor("config.json");
        File.WriteAllBytes(path, Json.Serialize(Config()));
        Assert.Equal(Config().Remote, MedalSyncConfig.Load(path).Remote);
        Assert.Throws<UnsafeOperationException>(() => MedalSyncConfig.Load(_fixture.PathFor("missing.json")));
        File.WriteAllText(path, "{\"format\":1,\"unexpected\":true}");
        Assert.Throws<InvalidDataException>(() => MedalSyncConfig.Load(path));
    }

    [Fact]
    public void MountPlan_ShowsAValidatedCommandWithASessionPlaceholder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = new LocalState(_fixture.PathFor("state"));
        Directory.CreateDirectory(state.Root);
        File.WriteAllBytes(state.ConfigPath, Json.Serialize(Config() with { RclonePath = @"C:\Program Files\rclone\rclone.exe" }));
        var output = new StringWriter();

        Assert.Equal(0, SessionCommands.WriteMountPlan(state, output));

        string plan = output.ToString();
        string log = Path.Combine(state.LogDirectory, "rclone-<session>.log");
        Assert.Contains("\"C:\\Program Files\\rclone\\rclone.exe\" mount ", plan, StringComparison.Ordinal);
        Assert.Contains(@" --cache-dir E:\MedalCache\0123456789abcdef0123456789abcdef\<session> ", plan, StringComparison.Ordinal);
        Assert.Contains(" --log-file " + (log.Contains(' ', StringComparison.Ordinal) ? $"\"{log}\"" : log) + " ", plan, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('0', 32), plan, StringComparison.Ordinal);
        Assert.DoesNotContain("--rc-pass", plan, StringComparison.Ordinal);
        // Read-only: no device identity, session login or log directory is created.
        Assert.Equal(["config.json"], Directory.GetFileSystemEntries(state.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void LocalState_KeepsAStableDeviceIdAndPrivateSessionLogins()
    {
        var state = new LocalState(_fixture.PathFor("state"));
        var device = state.Device();
        Assert.Equal(device, state.Device());
        // A copied profile on a differently named PC is not the same device.
        Assert.Throws<UnsafeOperationException>(() => state.Device("OTHER-PC (someone)"));
        Assert.True(CoordinationStore.IsValidId(device.DeviceId));

        string session = SessionCoordinator.NewId();
        var secret = state.SessionSecret(session, DateTimeOffset.UnixEpoch);
        Assert.Equal(secret, state.SessionSecret(session, DateTimeOffset.UnixEpoch.AddDays(1)));
        Assert.Equal(48, secret.Password.Length);
        state.ForgetSession(session);
        Assert.NotEqual(secret.Password, state.SessionSecret(session, DateTimeOffset.UnixEpoch).Password);
        Assert.Throws<ArgumentException>(() => state.SessionSecret(@"..\escape", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Syncthing_ReportsMissingAndPresentExclusions()
    {
        string root = _fixture.PathFor("LarsonServer");
        string media = _fixture.PathFor("Larson Drive", "Media");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(Path.Combine(root, "drive-sync", "config"));
        File.WriteAllText(Path.Combine(root, "drive-sync", "config", "config.xml"),
            $"<configuration><folder id=\"larson-drive-media\" label=\"Larson Drive Media\" path=\"{media}\" type=\"sendreceive\"/>"
            + "<folder id=\"larson-drive-documents\" label=\"Docs\" path=\"C:\\docs\" type=\"sendreceive\"/></configuration>");
        File.WriteAllLines(Path.Combine(media, ".stignore"), ["(?i)*.tmp", ".usb-drive-check-*"]);
        var exclusion = new SyncExclusion("larson-drive-media", "/Videos/Clips");

        var folders = SyncthingCheck.Folders(root);
        Assert.Equal(2, folders.Count);
        Assert.False(SyncthingCheck.Check(folders, [exclusion]).Single().Present);

        File.AppendAllLines(Path.Combine(media, ".stignore"), ["/Videos/Clips"]);
        Assert.True(SyncthingCheck.Check(folders, [exclusion]).Single().Present);
        Assert.True(SyncthingCheck.Check(folders, [new SyncExclusion("not-here", "/x")]).Single().Present);
    }

    [Fact]
    public void Log_RotatesWithinItsCap()
    {
        string directory = _fixture.PathFor("logs");
        var log = new RotatingLog(directory, maxBytes: 200, files: 3);
        for (int i = 0; i < 40; i++) log.Write($"line {i} {new string('x', 40)}");

        var files = Directory.GetFiles(directory);
        Assert.Equal(3, files.Length);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length < 400));
        Assert.Contains("line 39", File.ReadAllText(Path.Combine(directory, "medal-sync.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_UsesThePlanWordsFromEachPcsView()
    {
        var me = new DeviceIdentity(new string('a', 32), "Me");
        var other = new DeviceIdentity(new string('b', 32), "Other");
        var journal = Ownership.Initial(new string('0', 32), "12345", DateTimeOffset.UnixEpoch);
        Assert.Equal("Ready", StatusText.Label(journal, me));
        var active = journal with { State = OwnershipState.Active, Owner = other, SessionId = new string('1', 32) };
        Assert.Equal("Waiting for the other PC", StatusText.Label(active, me));
        Assert.Equal("Recording/Active", StatusText.Label(active, other));
        Assert.Equal("Uploading", StatusText.Label(active with { State = OwnershipState.Draining }, other));
        Assert.Equal("Recovery needed", StatusText.Label(active with { State = OwnershipState.RecoveryRequired }, me));
        Assert.StartsWith("Recovery needed:", StatusText.For(active, other, clipDrivePresent: false), StringComparison.Ordinal);
        Assert.Contains("still owns generation", StatusText.For(active, other, clipDrivePresent: false), StringComparison.Ordinal);
        Assert.StartsWith("Waiting for the other PC:", StatusText.For(active, me, clipDrivePresent: false), StringComparison.Ordinal);
        Assert.StartsWith("Recording/Active:", StatusText.For(active, other, clipDrivePresent: true), StringComparison.Ordinal);
        Assert.StartsWith("Ready:", StatusText.For(journal, me, clipDrivePresent: false), StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledState_RequiresBothConfigurationAndDeviceBesideBin()
    {
        string root = _fixture.PathFor("configured-installation");
        string bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        Assert.Null(LocalState.InstalledRoot(bin));
        File.WriteAllText(Path.Combine(root, "config.json"), "{}");
        Assert.Null(LocalState.InstalledRoot(bin));
        File.WriteAllText(Path.Combine(root, "device.json"), "{}");
        Assert.Equal(root, LocalState.InstalledRoot(bin + Path.DirectorySeparatorChar));
        Assert.Null(LocalState.InstalledRoot(Path.Combine(root, "publish")));
    }

    [Fact]
    public void MedalEnvironment_FindsExactlyOneSignedInLibrary()
    {
        string data = _fixture.PathFor("Roaming", "Medal");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "medal-guest.db"), "");
        Assert.Throws<UnsafeOperationException>(() => MedalEnvironment.Locate(_fixture.PathFor("Local", "Medal"), data));
        File.WriteAllText(Path.Combine(data, "medal-60098.db"), "");
        Assert.Equal("60098", MedalEnvironment.Locate(_fixture.PathFor("Local", "Medal"), data).AccountId);
        File.WriteAllText(Path.Combine(data, "medal-777.db"), "");
        Assert.Throws<UnsafeOperationException>(() => MedalEnvironment.Locate(_fixture.PathFor("Local", "Medal"), data));
    }

    [Fact]
    public void InventoryText_separates_gate_b_blockers_from_clip_move_blockers()
    {
        var exclusion = new SyncExclusion("larson-drive-media", "/Videos/Clips");
        InventoryReport Report(bool excluded, string winFsp) => new(
            DateTimeOffset.UnixEpoch, "PC", "user", "Windows", "1", "2", MedalRunning: false,
            Library: null, LibraryError: "not found", CaptureTree: null, Drives: [], ProposedDrive: "M:\\",
            ProposedDriveFree: true, Capacity: null, StartupEntries: new Dictionary<string, string?>(),
            SyncFolders: [], SyncExclusions: [new ExclusionStatus(exclusion, null, excluded, "detail")],
            Dependencies: new Dictionary<string, string> { ["WinFsp"] = winFsp },
            Server: new Dictionary<string, string>());

        string text = InventoryText.Render(Report(excluded: false, winFsp: "not installed"));
        int gateB = text.IndexOf("Blockers before Gate B:", StringComparison.Ordinal);
        int clipsMove = text.IndexOf("Blockers before any clip moves (Phase 4):", StringComparison.Ordinal);
        Assert.True(gateB >= 0 && clipsMove > gateB, text);
        Assert.Contains("WinFsp is not installed.", text[gateB..clipsMove]);
        Assert.Contains("must ignore /Videos/Clips", text[clipsMove..]);

        string ready = InventoryText.Render(Report(excluded: true, winFsp: "installed"));
        Assert.DoesNotContain("before any clip moves", ready);
        Assert.Contains("Blockers before Gate B:", ready); // the unreadable library still blocks
    }
}

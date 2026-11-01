using System.Text;
using System.Runtime.InteropServices;
using MedalSync.Cli;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class LauncherAndRelocationTests : IDisposable
{
    private static readonly WindowsPath OldFolder = WindowsPath.Parse(@"D:\Clips");
    private static readonly WindowsPath Capture = WindowsPath.Parse(@"M:\Clips");
    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private string Library()
    {
        string db = _fixture.CreateLibrary("pc");
        // A cloud-only entry whose thumbnail Medal's move left behind, as on the first PC.
        MedalFixture.AddContent(db, "cloud", thumbnail: @"D:\Clips\Thumbnails\cloud.jpg");
        MedalFixture.AddContent(db, "moved", video: @"M:\Clips\Clips\a.mp4", thumbnail: @"M:\Clips\Thumbnails\a.jpg");
        MedalFixture.AddContent(db, "mixed-case", video: @"d:\clips\Clips\b.mp4");
        MedalFixture.AddContent(db, "elsewhere", video: @"E:\Other\c.mp4");
        return db;
    }

    [Fact]
    public void Relocation_PlansOnlyPathsUnderTheOldFolder()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(Library(), MedalFixture.AccountId);

        var moves = ReferenceRelocation.Plan(snapshot, OldFolder, Capture);

        Assert.Equal(2, moves.Count);
        var cloud = Assert.Single(moves, m => m.ContentId == "cloud");
        Assert.Equal("thumbnail_path", cloud.Column);
        Assert.Equal(@"M:\Clips\Thumbnails\cloud.jpg", cloud.To.Display);
        Assert.Equal(@"M:\Clips\Clips\b.mp4", Assert.Single(moves, m => m.ContentId == "mixed-case").To.Display);
    }

    [Fact]
    public void Relocation_RewritesOnlyThePlannedCellsAndRestoresThroughTheGuard()
    {
        string db = Library();
        var snapshot = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        var moves = ReferenceRelocation.Plan(snapshot, OldFolder, Capture);

        var relocated = ReferenceRelocation.Apply(snapshot, moves);
        LibraryRestorer.Restore(db, relocated, new RestoreOptions(MedalFixture.AccountId, _fixture.PathFor("backups")), DateTimeOffset.UtcNow);

        var after = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        Assert.Equal(relocated.Digest(), after.Digest());
        Assert.Empty(ReferenceRelocation.Plan(after, OldFolder, Capture));
        var report = AssetInventory.Check(AssetInventory.Collect(after), [Capture, WindowsPath.Parse(@"E:\Other")], new ExistingFiles());
        Assert.DoesNotContain(report.Items, i => i.Problem == AssetProblem.OutsideAllowedRoots);
        // Rows and cells that did not move are identical.
        var contents = after.Table("contents");
        int id = contents.ColumnIndex("local_content_id");
        var before = snapshot.Table("contents").Rows.Single(r => TableSnapshot.Text(r[id]) == "moved");
        Assert.Equal(before, contents.Rows.Single(r => TableSnapshot.Text(r[id]) == "moved"));
        Assert.Equal(snapshot.Table("content_counts").Rows, after.Table("content_counts").Rows);
    }

    [Fact]
    public void Relocation_ChangesNothingWhenTheLibraryMovedOnSincePlanning()
    {
        string db = Library();
        var planned = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        var moves = ReferenceRelocation.Plan(planned, OldFolder, Capture);
        var stale = moves.Select(m => m.ContentId == "cloud" ? m with { Original = @"D:\Clips\Thumbnails\other.jpg" } : m).ToList();

        Assert.Throws<UnsafeOperationException>(() => ReferenceRelocation.Apply(planned, stale));
    }

    [Fact]
    public void Relocation_RefusesFoldersThatContainEachOther()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(Library(), MedalFixture.AccountId);

        Assert.Throws<UnsafeOperationException>(() => ReferenceRelocation.Plan(snapshot, WindowsPath.Parse(@"M:\Clips\Old"), Capture));
    }

    private sealed class ExistingFiles : IFileProbe
    {
        public FileProbeResult Probe(WindowsPath path, WindowsPath root) => new(true, false, 1, DateTime.UnixEpoch, false);
    }

    [Theory]
    [InlineData(new string[0], false, null)]
    [InlineData(new[] { "--at-sign-in" }, true, null)]
    [InlineData(new[] { "medal://clip/abc?x=1" }, false, "medal://clip/abc?x=1")]
    [InlineData(new[] { "MEDAL://Upper" }, false, "MEDAL://Upper")]
    public void Launcher_AcceptsItsThreeForms(string[] args, bool atSignIn, string? link)
    {
        Assert.Equal(new LauncherRequest(atSignIn, link), Launcher.Parse(args));
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("https://example.com")]
    [InlineData("medal://a\" --inject")]
    [InlineData("medal://a\\")]
    [InlineData("medal://a\nb")]
    public void Launcher_RefusesAnythingElse(string arg)
    {
        Assert.ThrowsAny<Exception>(() => Launcher.Parse([arg]));
        Assert.Throws<UsageException>(() => Launcher.Parse(["--at-sign-in", "medal://x"]));
    }

    [Theory]
    [InlineData(false, false, "Launch")]
    [InlineData(true, false, "Launch")]
    [InlineData(true, true, "Forward")]
    [InlineData(false, true, "Busy")]
    public void Launcher_DecidesFromWhatIsRunning(bool medal, bool supervisor, string expected)
    {
        Assert.Equal(Enum.Parse<LauncherStep>(expected), Launcher.Decide(medal, supervisor));
    }

    [Fact]
    public void Launcher_WaitsLongerForTheServerAtSignIn()
    {
        Assert.Equal(["launch", "--wait-for-server", "120"], Launcher.LaunchArguments(new LauncherRequest(true, null)));
        Assert.Equal(["launch", "--wait-for-server", "20"], Launcher.LaunchArguments(new LauncherRequest(false, null)));
        Assert.Equal(["launch", "--wait-for-server", "20", "--medal-link", "medal://x"],
            Launcher.LaunchArguments(new LauncherRequest(false, "medal://x")));
    }

    [Fact]
    public void Launcher_ShowsOnlyOutcomesThatNeedTheUser()
    {
        Assert.Null(Launcher.Outcome(0, "launch: Started: Medal started.\nfinish: Published: done", ""));
        string blocked = Launcher.Outcome(1, "launch: Blocked: The server cannot be reached.\n", "")!;
        Assert.EndsWith("The server cannot be reached.", blocked, StringComparison.Ordinal);
        Assert.StartsWith("Medal was not started", blocked, StringComparison.Ordinal);
        Assert.Equal("Uploads are still running.", Launcher.Outcome(3, "finish: Pending: Uploads are still running.", ""));
        Assert.EndsWith("medal-sync is already running on this PC.",
            Launcher.Outcome(1, "", "medal-sync: medal-sync is already running on this PC.\n"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"""C:\Users\u\AppData\Local\LarsonServer\MedalSync\bin\medal-sync-launch.exe"" --at-sign-in", true, false)]
    [InlineData(@"""C:\Users\u\AppData\Local\Medal\current\Medal.exe""", false, true)]
    [InlineData(@"""C:\Users\u\AppData\Local\Medal\current\medal.exe"" ""%1""", false, true)]
    [InlineData(@"C:\Tools\MedalTools.exe", false, false)]
    public void StartupEntries_TellTheLauncherFromMedalItself(string command, bool launcher, bool direct)
    {
        Assert.Equal(launcher, StartupEntries.StartsLauncher(command));
        Assert.Equal(direct, StartupEntries.StartsMedalDirectly(command));
    }

    [Fact]
    public void StartupEntries_ReadShortcutTargetsInEitherEncoding()
    {
        byte[] Shortcut(Encoding encoding, string target) => [.. new byte[] { 0x4C, 0, 0, 0 }, .. encoding.GetBytes(target), 0, 0];

        Assert.True(StartupEntries.ShortcutStartsLauncher(Shortcut(Encoding.Unicode, @"C:\x\MEDAL-SYNC-LAUNCH.EXE")));
        Assert.True(StartupEntries.ShortcutStartsLauncher(Shortcut(Encoding.ASCII, @"C:\x\medal-sync-launch.exe")));
        Assert.False(StartupEntries.ShortcutStartsLauncher(Shortcut(Encoding.Unicode, @"C:\Medal\current\Medal.exe")));
        Assert.Equal(@"""C:\b\medal-sync-launch.exe"" --at-sign-in", StartupEntries.RunCommand(@"C:\b\medal-sync-launch.exe"));
        Assert.Equal(@"""C:\b\medal-sync-launch.exe"" ""%1""", StartupEntries.ProtocolCommand(@"C:\b\medal-sync-launch.exe"));
    }

    [Fact]
    public void InstalledCopyOwnsStartupAndReplacesASupersededPortableLauncher()
    {
        string state = _fixture.PathFor("state"), native = _fixture.PathFor("Programs", "Medal Sync", "bin");
        string portable = Path.Combine(state, "bin");
        string portableLauncher = Path.Combine(portable, StartupEntries.LauncherFile);
        string nativeLauncher = Path.Combine(native, StartupEntries.LauncherFile);
        Directory.CreateDirectory(portable);
        Directory.CreateDirectory(native);
        File.WriteAllText(portableLauncher, "synthetic");
        File.WriteAllText(nativeLauncher, "synthetic");

        // An installed copy always uses its own launcher, even if the Run value names an older one that still exists.
        Assert.Equal(nativeLauncher, StartupEntries.GuardLauncher(native, true, StartupEntries.RunCommand(portableLauncher)));
        Assert.Equal(nativeLauncher, StartupEntries.GuardLauncher(native, true, StartupEntries.RunCommand(nativeLauncher)));
        Assert.Equal(nativeLauncher, StartupEntries.GuardLauncher(native, true, @"""C:\Medal\Medal.exe"""));
        Assert.Equal(nativeLauncher, StartupEntries.GuardLauncher(native, true, null));

        // A portable copy keeps an existing target (including a native one) and otherwise uses itself.
        Assert.Equal(nativeLauncher, StartupEntries.GuardLauncher(portable, false, StartupEntries.RunCommand(nativeLauncher)));
        Assert.Equal(portableLauncher, StartupEntries.GuardLauncher(portable, false, StartupEntries.RunCommand(nativeLauncher) + " --unexpected"));
        Assert.Equal(portableLauncher, StartupEntries.GuardLauncher(portable, false, null));

        File.Delete(nativeLauncher);
        Assert.Null(StartupEntries.GuardLauncher(native, true, StartupEntries.RunCommand(portableLauncher)));
        Assert.Equal(portableLauncher, StartupEntries.GuardLauncher(portable, false, StartupEntries.RunCommand(nativeLauncher)));
    }

    [Theory]
    [InlineData(@"""C:\Users\u\.medal-sync\bin\medal-sync-launch.exe"" --at-sign-in", true)]
    [InlineData(@"""C:\Users\u\.medal-sync\bin\medal-sync-launch.exe"" ""%1""", true)]
    [InlineData(@"C:\Users\u\.medal-sync\bin\medal-sync-launch.exe", true)]
    [InlineData(@"""C:\Programs\Medal Sync\bin\medal-sync-launch.exe"" ""%1""", false)]
    [InlineData(@"C:\PROGRAMS\Medal Sync\bin\MEDAL-SYNC-LAUNCH.EXE", false)]
    [InlineData(@"""C:\Medal\current\Medal.exe"" ""%1""", false)]
    [InlineData(null, false)]
    public void StartupEntries_DetectEntriesLeftOnAnotherLauncher(string? command, bool other)
    {
        Assert.Equal(other, StartupEntries.StartsOtherLauncher(command, @"C:\Programs\Medal Sync\bin\medal-sync-launch.exe"));
    }

    [Fact]
    public void StartupEntries_ReadOnlyTheExactRunSyntax()
    {
        Assert.Equal(@"C:\b\medal-sync-launch.exe", StartupEntries.RunTarget(@"""C:\b\medal-sync-launch.exe"" --at-sign-in"));
        Assert.Null(StartupEntries.RunTarget(@"""C:\b\medal-sync-launch.exe"" --at-sign-in --x"));
        Assert.Null(StartupEntries.RunTarget(@"""C:\b\Medal.exe"" --at-sign-in"));
        Assert.Null(StartupEntries.RunTarget(@"""medal-sync-launch.exe"" --at-sign-in"));
        Assert.Null(StartupEntries.RunTarget(@"C:\b\medal-sync-launch.exe --at-sign-in"));
        Assert.Null(StartupEntries.RunTarget(null));
    }

    [Theory]
    [InlineData(null, @"C:\Medal\current\Medal.exe,0")]
    [InlineData(",0", @"C:\Medal\current\Medal.exe,0")]
    [InlineData(@"C:\Medal\current\Medal.exe,0", @"C:\Medal\current\Medal.exe,0")]
    [InlineData(@"C:\Medal\app.ico,0", @"C:\Medal\app.ico,0")]
    public void StartupEntries_RepairARecreatedWindowsShortcutAndPreserveItsIcon(string? icon, string expectedIcon)
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = _fixture.PathFor("Medal.lnk");
        string launcher = _fixture.PathFor("medal-sync-launch.exe");
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        object? shortcut = null;
        object? restored = null;
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = @"C:\Medal\current\Medal.exe";
            link.Arguments = "--old";
            if (icon is not null) link.IconLocation = icon;
            link.Save();

            Assert.True(StartupEntries.RepairShortcut(path, launcher));
            restored = ((dynamic)shell).CreateShortcut(path);
            dynamic after = restored;
            Assert.Equal(launcher, (string)after.TargetPath);
            Assert.Equal("", (string)after.Arguments);
            Assert.Equal(expectedIcon, (string)after.IconLocation);
            Assert.False(StartupEntries.RepairShortcut(path, launcher));

            after.TargetPath = @"C:\Other\Unrelated.exe";
            after.Save();
            Assert.False(StartupEntries.RepairShortcut(path, launcher));
        }
        finally
        {
            if (restored is not null) Marshal.FinalReleaseComObject(restored);
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    [Fact]
    public void StartupEntries_RepointAShortcutLeftOnASupersededLauncherAndKeepItsArguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = _fixture.PathFor("Medal Status.lnk");
        string old = _fixture.PathFor("old", "bin", StartupEntries.LauncherFile);
        string launcher = _fixture.PathFor("Programs", "Medal Sync", "bin", StartupEntries.LauncherFile);
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        object? shortcut = null;
        object? restored = null;
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = old;
            link.Arguments = "--status";
            link.IconLocation = old + ",0";
            link.Save();

            Assert.True(StartupEntries.RepairShortcut(path, launcher));
            restored = ((dynamic)shell).CreateShortcut(path);
            dynamic after = restored;
            Assert.Equal(launcher, (string)after.TargetPath, ignoreCase: true);
            Assert.Equal("--status", (string)after.Arguments);
            Assert.Equal(launcher + ",0", (string)after.IconLocation, ignoreCase: true);
            Assert.Equal(Path.GetDirectoryName(launcher), (string)after.WorkingDirectory, ignoreCase: true);
            Assert.False(StartupEntries.RepairShortcut(path, launcher));
        }
        finally
        {
            if (restored is not null) Marshal.FinalReleaseComObject(restored);
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private sealed class StepClock : IClock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;

        public int Sleeps { get; private set; }

        public void Sleep(TimeSpan duration)
        {
            Sleeps++;
            Now += duration;
        }
    }

    [Fact]
    public void Waiting_StopsAsSoonAsReadyOrAtTheDeadline()
    {
        var clock = new StepClock();
        int calls = 0;
        Assert.True(Waiting.Until(() => ++calls == 3, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), clock));
        Assert.Equal(2, clock.Sleeps);

        var late = new StepClock();
        Assert.False(Waiting.Until(() => false, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(5), late));
        Assert.Equal(3, late.Sleeps);
        Assert.True(Waiting.Until(() => true, TimeSpan.Zero, TimeSpan.FromSeconds(5), new StepClock()));
    }
}

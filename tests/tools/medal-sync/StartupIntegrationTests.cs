using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MedalSync.Cli;
using MedalSync.Platform;
using MedalSync.Tests.Fixtures;
using Microsoft.Win32;

namespace MedalSync.Tests;

/// <summary>
/// Turning sign-in startup on and off from an installed copy, against a scratch
/// HKCU key standing in for HKCU\Software and scratch shortcut folders.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupIntegrationTests : IDisposable
{
    private const string Medal = @"C:\Users\u\AppData\Local\Medal\current\Medal.exe";
    private readonly MedalFixture _fixture = new();
    private readonly string _keyPath = @"Software\MedalSyncTests\" + Guid.NewGuid().ToString("N");
    private readonly string _state, _launcher, _programs, _desktop;
    private readonly RegistryKey? _software;

    public StartupIntegrationTests()
    {
        _state = _fixture.PathFor("state");
        _launcher = _fixture.PathFor("Programs", "Medal Sync", "bin", StartupEntries.LauncherFile);
        _programs = _fixture.PathFor("shortcuts", "Programs");
        _desktop = _fixture.PathFor("shortcuts", "Desktop");
        foreach (string dir in new[] { _state, Path.GetDirectoryName(_launcher)!, _programs, _desktop }) Directory.CreateDirectory(dir);
        File.WriteAllText(_launcher, "synthetic");
        File.WriteAllText(Path.Combine(_state, "config.json"), "{}");
        if (OperatingSystem.IsWindows()) _software = Registry.CurrentUser.CreateSubKey(_keyPath);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _software?.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        }
        _fixture.Dispose();
    }

    private StartupIntegration Startup(int minute = 0)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // The integration disposes the key it is given; hand it its own handle.
        return new StartupIntegration(_state, _launcher, Registry.CurrentUser.CreateSubKey(_keyPath), [_programs, _desktop], Medal,
            () => new DateTimeOffset(2026, 11, 1, 12, minute, 0, TimeSpan.Zero));
    }

    private RegistryKey Key(string sub) => OperatingSystem.IsWindows() ? _software!.CreateSubKey(sub) : throw new PlatformNotSupportedException();

    private static void MakeShortcut(string path, string target, string icon = "")
    {
        if (!OperatingSystem.IsWindows()) return;
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        object? link = null;
        try
        {
            link = ((dynamic)shell).CreateShortcut(path);
            dynamic l = link!;
            l.TargetPath = target;
            if (icon.Length > 0) l.IconLocation = icon;
            l.Save();
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    [Fact]
    public void ApplyRoutesMedalThroughTheLauncherAndRestorePutsTheOriginalsBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var run = Key(StartupEntries.RunSubkey))
        {
            run.SetValue("Medal", $"\"{Medal}\"");
            run.SetValue("Other", @"C:\Tools\Other.exe");
        }
        using (var protocol = Key(StartupEntries.ProtocolSubkey)) protocol.SetValue(null, $"\"{Medal}\" \"%1\"");
        string shortcut = Path.Combine(_programs, "Medal.lnk");
        MakeShortcut(shortcut, Medal);

        using (var startup = Startup())
        {
            Assert.False(startup.Enabled);
            var preview = startup.Preview();
            Assert.Contains(preview, l => l.Contains("'Medal'", StringComparison.Ordinal));
            Assert.Contains(preview, l => l.Contains("medal://", StringComparison.Ordinal));
            Assert.Contains(preview, l => l.Contains(shortcut, StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(Path.Combine(_state, StartupEntries.OriginalsFile))); // Preview writes nothing.

            startup.Apply();
            Assert.True(startup.Enabled);
        }
        using (var run = Key(StartupEntries.RunSubkey))
        {
            Assert.Null(run.GetValue("Medal"));
            Assert.Equal(@"C:\Tools\Other.exe", run.GetValue("Other"));
            Assert.Equal(StartupEntries.RunCommand(_launcher), run.GetValue(StartupEntries.RunValue));
        }
        using (var protocol = Key(StartupEntries.ProtocolSubkey)) Assert.Equal(StartupEntries.ProtocolCommand(_launcher), protocol.GetValue(null));
        Assert.Equal(_launcher, StartupEntries.ShortcutTarget(shortcut), ignoreCase: true);
        var saved = Util.Json.ReadFile<StartupOriginals>(Path.Combine(_state, StartupEntries.OriginalsFile), "originals");
        Assert.Equal(Medal, saved.MedalExecutable);
        Assert.Equal(["Medal"], saved.RunValues.Select(v => v.Name));
        Assert.True(File.Exists(Assert.Single(saved.Shortcuts).Backup));

        // Applying again keeps the first saved originals and changes nothing more.
        using (var startup = Startup(1)) Assert.Empty(startup.Apply());

        using (var startup = Startup(2))
        {
            startup.Restore();
            Assert.False(startup.Enabled);
        }
        using (var run = Key(StartupEntries.RunSubkey))
        {
            Assert.Equal($"\"{Medal}\"", run.GetValue("Medal"));
            Assert.Null(run.GetValue(StartupEntries.RunValue));
            Assert.Equal(@"C:\Tools\Other.exe", run.GetValue("Other"));
        }
        using (var protocol = Key(StartupEntries.ProtocolSubkey)) Assert.Equal($"\"{Medal}\" \"%1\"", protocol.GetValue(null));
        Assert.Equal(Medal, StartupEntries.ShortcutTarget(shortcut), ignoreCase: true);
        Assert.Single(Directory.GetFiles(_state, StartupEntries.OriginalsFile + ".restored-20261101-120200"));
    }

    [Fact]
    public void RestoreRetargetsOnlyMedalShortcutsAndKeepsMedalSyncOwnShortcuts()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var startup = Startup()) startup.Apply();
        string added = Path.Combine(_desktop, "Medal.lnk");
        MakeShortcut(added, _launcher, Medal + ",0"); // A Medal update recreated it; the launcher repointed it, keeping Medal's icon.
        string companion = Path.Combine(_desktop, "Medal Sync.lnk"); // Setup's own desktop shortcut.
        MakeShortcut(companion, _launcher);
        string companionWithMedalIcon = Path.Combine(_programs, "Medal Sync Status.lnk");
        MakeShortcut(companionWithMedalIcon, _launcher, Medal + ",0");
        string recorded = Path.Combine(_programs, "Medal Library.lnk");
        string kept = Path.Combine(_desktop, "Medal Library.lnk");
        MakeShortcut(recorded, _launcher);
        MakeShortcut(kept, _launcher);
        File.WriteAllText(Path.Combine(_state, StartupIntegration.LibraryShortcutsFile),
            $$"""{"format":1,"created":[{{System.Text.Json.JsonSerializer.Serialize(recorded)}},"C:\\Elsewhere\\Medal Library.lnk"]}""");

        using (var startup = Startup(5)) startup.Restore();

        Assert.Equal(Medal, StartupEntries.ShortcutTarget(added), ignoreCase: true);
        Assert.Equal(_launcher, StartupEntries.ShortcutTarget(companion), ignoreCase: true);
        Assert.Equal(_launcher, StartupEntries.ShortcutTarget(companionWithMedalIcon), ignoreCase: true);
        Assert.False(File.Exists(recorded));
        Assert.Equal(_launcher, StartupEntries.ShortcutTarget(kept), ignoreCase: true);
        Assert.False(File.Exists(Path.Combine(_state, StartupIntegration.LibraryShortcutsFile)));
    }

    [Fact]
    public void ApplyRefusesBeforeConfigurationAndRestoreRefusesWhenOff()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.Delete(Path.Combine(_state, "config.json"));
        using var startup = Startup();
        Assert.Contains("Configure Medal Sync", Assert.Throws<MedalSyncException>(() => startup.Apply()).Message, StringComparison.Ordinal);
        Assert.Throws<MedalSyncException>(() => startup.Restore());
        using (var run = Key(StartupEntries.RunSubkey)) Assert.Empty(run.GetValueNames());
        Assert.False(File.Exists(Path.Combine(_state, StartupEntries.OriginalsFile)));
    }

    [Fact]
    public void RestoreWaitsForARepairInProgressAndALaterRepairLeavesItOff()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var run = Key(StartupEntries.RunSubkey)) run.SetValue("Medal", $"\"{Medal}\"");
        using (var startup = Startup()) startup.Apply();

        // A repair holding the lock (as the supervisor's timer would) blocks the restore until it finishes.
        // Mutexes belong to threads, so these are plain threads rather than tasks.
        using var repairing = new ManualResetEvent(false);
        using var release = new ManualResetEvent(false);
        Exception? failure = null;
        var repair = new Thread(() =>
        {
            using var held = StartupEntries.Lock();
            repairing.Set();
            release.WaitOne();
        });
        repair.Start();
        Assert.True(repairing.WaitOne(TimeSpan.FromSeconds(10)));
        var restore = new Thread(() =>
        {
            try { using var startup = Startup(1); startup.Restore(); }
            catch (Exception ex) { failure = ex; }
        });
        restore.Start();
        Assert.False(restore.Join(300));
        Assert.True(File.Exists(Path.Combine(_state, StartupEntries.OriginalsFile)));
        release.Set();
        Assert.True(repair.Join(TimeSpan.FromSeconds(10)));
        Assert.True(restore.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);

        // The originals file is retired, so the next repair changes nothing.
        using (var software = Registry.CurrentUser.CreateSubKey(_keyPath))
        {
            Assert.Empty(StartupEntries.Enforce(_state, _launcher, software, StartupEntries.Shortcuts([_programs, _desktop])));
        }
        using (var run = Key(StartupEntries.RunSubkey))
        {
            Assert.Equal($"\"{Medal}\"", run.GetValue("Medal"));
            Assert.Null(run.GetValue(StartupEntries.RunValue));
        }
    }

    [Fact]
    public void StartupLockIsExclusiveAcrossThreadsAndReentrantOnOne()
    {
        using var held = StartupEntries.Lock();
        using (StartupEntries.Lock(TimeSpan.Zero)) { } // Same thread: re-entrant.
        Exception? seen = null;
        var other = new Thread(() =>
        {
            try { StartupEntries.Lock(TimeSpan.Zero).Dispose(); }
            catch (Exception ex) { seen = ex; }
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<IOException>(seen);
    }

    [Theory]
    [InlineData(new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, true)]
    [InlineData(new byte[] { 1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, true)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(null, false)]
    public void ReportsButNeverChangesWindowsStartupAppsSwitch(byte[]? approved, bool off)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (approved is not null) using (var key = Key(StartupEntries.StartupApprovedSubkey)) key.SetValue(StartupEntries.RunValue, approved);
        using var startup = Startup();
        Assert.Equal(off, startup.TurnedOffInWindows);
        var done = startup.Apply();
        Assert.Equal(off, done.Contains(StartupIntegration.TurnedOffMessage));
        using var after = Key(StartupEntries.StartupApprovedSubkey);
        Assert.Equal(approved, after.GetValue(StartupEntries.RunValue) as byte[]);
    }

    [Fact]
    public void CommandPreviewsByDefaultAndRejectsBothActions()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var startup = Startup();
        var output = new StringWriter();
        Assert.Equal(0, StartupCommands.Run(startup, apply: false, restore: false, output));
        Assert.Contains("Preview only", output.ToString(), StringComparison.Ordinal);
        Assert.False(startup.Enabled);
        var error = new StringWriter();
        Assert.Equal(2, CommandRunner.Run(["startup", "--apply", "--restore"], new StringWriter(), error));
        Assert.Contains("not both", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"""C:\M\Medal.exe"" --hidden", @"C:\M\Medal.exe")]
    [InlineData(@"""C:\M\medal.exe""", @"C:\M\medal.exe")]
    [InlineData(@"C:\Tools\Other.exe", null)]
    public void ReadsMedalFromARunCommand(string command, string? medal) => Assert.Equal(medal, StartupIntegration.MedalIn(command));
}

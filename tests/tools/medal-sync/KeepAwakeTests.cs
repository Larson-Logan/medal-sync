using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class KeepAwakeTests
{
    private static MedalSyncConfig Config => new(1, "0123456789abcdef0123456789abcdef", "12345", @"D:\Coordination", @"D:\Media",
        @"M:\Clips", @"E:\Cache", @"C:\Programs\rclone.exe", new(5, 20, 20, 300, 30, 1000, 2, 53684), 4, []) { StorageMode = StorageMode.Local };

    [Fact]
    public void TheSettingIsOnByDefaultAndLeftOutOfTheFileUntilSet()
    {
        string json = System.Text.Encoding.UTF8.GetString(Json.Serialize(Config));
        Assert.DoesNotContain("keepAwakeWhileFinishing", json, StringComparison.Ordinal);
        Assert.NotNull(SessionCommands.KeepAwake(Config, _ => { }));

        var off = Config with { KeepAwakeWhileFinishing = false };
        string offJson = System.Text.Encoding.UTF8.GetString(Json.Serialize(off));
        Assert.Contains("\"keepAwakeWhileFinishing\": false", offJson, StringComparison.Ordinal);
        Assert.False(Json.Deserialize<MedalSyncConfig>(Json.Serialize(off), "configuration").KeepAwakeWhileFinishing);
        Assert.Null(SessionCommands.KeepAwake(off, _ => { }));
        Assert.NotNull(SessionCommands.KeepAwake(Config with { KeepAwakeWhileFinishing = true }, _ => { }));
    }

    [Fact]
    public void AHoldReleasesExactlyOnce()
    {
        int releases = 0;
        var hold = new KeepAwakeHold(() => releases++);
        hold.Dispose();
        hold.Dispose();
        Assert.Equal(1, releases);
    }

    [Fact]
    public void ARefusedRequestIsReportedAsNoHold()
    {
        Assert.Same(KeepAwakeHold.None, KeepAwakeHold.Take(new Throwing()));
        Assert.Same(KeepAwakeHold.None, KeepAwakeHold.Take(null));
    }

    [Fact]
    public void WindowsRequestsNestAndReleaseWithTheLastHold()
    {
        var log = new List<string>();
        using var awake = new MedalSync.Platform.WindowsKeepAwake(log.Add);
        var first = awake.Hold();
        var second = awake.Hold();
        first.Dispose();
        second.Dispose();
        second.Dispose();
        using (awake.Hold()) { }
        // A real power request succeeds for a normal user; a refusal is logged once and never throws.
        Assert.True(log.Count <= 1);
    }

    private sealed class Throwing : IKeepAwake
    {
        public IDisposable Hold() => throw new InvalidOperationException("synthetic failure");
    }
}

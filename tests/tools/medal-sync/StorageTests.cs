using MedalSync.Storage;

namespace MedalSync.Tests;

public sealed class StorageTests
{
    private const string Remote = @"\\192.0.2.10\Data\Media\Videos";

    private static string Stats(string cache, string fs = "//192.0.2.10/Data/Media/Videos") =>
        $"{{\"fs\":\"{fs}\",\"inUse\":1,\"diskCache\":{{{cache}}},\"metadataCache\":{{\"dirs\":1,\"files\":0}}}}";

    private const string Idle = "\"bytesUsed\":0,\"erroredFiles\":0,\"files\":0,\"hashType\":1,\"outOfSpace\":false,\"uploadsInProgress\":0,\"uploadsQueued\":0";

    [Fact]
    public void Drain_IsDrainedOnlyWhenEveryCounterIsZero()
    {
        var verdict = VfsStatus.Evaluate(Stats(Idle), "{\"queue\":[]}", Remote);
        Assert.Equal(DrainState.Drained, verdict.State);
    }

    [Fact]
    public void Drain_ReportsPendingUploads()
    {
        string busy = Idle.Replace("\"uploadsQueued\":0", "\"uploadsQueued\":1", StringComparison.Ordinal);
        var verdict = VfsStatus.Evaluate(Stats(busy),
            "{\"queue\":[{\"name\":\"Clips/a.mp4\",\"id\":1,\"size\":1048576,\"expiry\":4.5,\"tries\":0,\"delay\":5,\"uploading\":false}]}", Remote);
        Assert.Equal(DrainState.Pending, verdict.State);
        Assert.Equal(1, verdict.QueuedFiles);
        Assert.Equal(1048576, verdict.QueuedBytes);
    }

    [Theory]
    [InlineData("\"erroredFiles\":0", "\"erroredFiles\":2")]
    [InlineData("\"outOfSpace\":false", "\"outOfSpace\":true")]
    public void Drain_WaitsForCacheCleanupWithoutDiagnosingFailedUploads(string from, string to)
    {
        var verdict = VfsStatus.Evaluate(Stats(Idle.Replace(from, to, StringComparison.Ordinal)), "{\"queue\":[]}", Remote);
        Assert.Equal(DrainState.Pending, verdict.State);
        Assert.True(verdict.CacheSpaceTrouble);
        Assert.Contains(verdict.Reasons, r => r.Contains("SSD cache", StringComparison.Ordinal));
        Assert.DoesNotContain(verdict.Reasons, r => r.Contains("failed to upload", StringComparison.Ordinal));
        Assert.Equal(DrainState.Drained, VfsStatus.Evaluate(Stats(Idle), "{\"queue\":[]}", Remote).State);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Drain_ReportsPreviousFailuresOnlyForWaitingQueueEntries(bool uploading, bool retrying)
    {
        string queue = $$"""{"queue":[{"id":1,"size":42,"tries":3,"uploading":{{(uploading ? "true" : "false")}}}]}""";
        var verdict = VfsStatus.Evaluate(Stats(Idle), queue, Remote);
        Assert.Equal(DrainState.Pending, verdict.State);
        Assert.Equal(retrying, verdict.Reasons.Any(r => r.Contains("being retried", StringComparison.Ordinal)));
        Assert.Equal(new UploadQueueItem(1, 3, uploading), Assert.Single(verdict.Uploads));
        if (retrying) Assert.Contains(verdict.Reasons, r => r.Contains("primary storage accepts writes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "{\"queue\":[]}")]
    [InlineData("{\"fs\":\"//192.0.2.10/Data/Media/Videos\"}", "{\"queue\":[]}")]
    [InlineData("not json", "{\"queue\":[]}")]
    [InlineData("STATS", null)]
    [InlineData("STATS", "{}")]
    [InlineData("STATS", "{\"queue\":[{\"name\":\"x\"}]}")]
    [InlineData("STATS", "{\"queue\":[{\"id\":1,\"size\":1,\"uploading\":false}]}")]
    [InlineData("STATS", "{\"queue\":[{\"id\":1,\"size\":1,\"tries\":-1,\"uploading\":false}]}")]
    [InlineData("STATS", "{\"queue\":[{\"id\":1,\"size\":1,\"tries\":3,\"uploading\":\"false\"}]}")]
    [InlineData("NOCOUNTER", "{\"queue\":[]}")]
    [InlineData("WRONGFS", "{\"queue\":[]}")]
    public void Drain_TreatsAnythingMissingOrUnexpectedAsUnknown(string? stats, string? queue)
    {
        stats = stats switch
        {
            "STATS" => Stats(Idle),
            "NOCOUNTER" => Stats(Idle.Replace(",\"uploadsQueued\":0", "", StringComparison.Ordinal)),
            "WRONGFS" => Stats(Idle, "C:/Users/someone/elsewhere"),
            _ => stats,
        };
        Assert.Equal(DrainState.Unknown, VfsStatus.Evaluate(stats, queue, Remote).State);
    }

    [Theory]
    [InlineData(@"\\?\UNC\192.0.2.10\Data\Media\Videos\")]
    [InlineData("//192.0.2.10/data/media/videos")]
    public void Drain_AcceptsEquivalentSpellingsOfTheRemote(string reported) =>
        Assert.True(VfsStatus.SameRemote(reported, Remote));

    private static MountSettings Settings() => new(
        Remote, 'M', @"E:\MedalCache\0123456789abcdef0123456789abcdef\11111111111111111111111111111111",
        TimeSpan.FromSeconds(5), 20L << 30, 20L << 30, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30),
        TimeSpan.FromHours(1000), 2, 53682, "Medal Clips", @"C:\Users\someone\AppData\Local\LarsonServer\MedalSync\logs\rclone.log");

    [Fact]
    public void Mount_BuildsTheReviewedCommandWithoutCredentials()
    {
        var args = MountPlan.Arguments(Settings());

        Assert.Equal(["mount", Remote, "M:"], args.Take(3));
        string line = string.Join(' ', args);
        Assert.Contains("--vfs-cache-mode full", line, StringComparison.Ordinal);
        Assert.Contains("--vfs-write-back 5s", line, StringComparison.Ordinal);
        Assert.Contains("--vfs-cache-max-size 20Gi", line, StringComparison.Ordinal);
        Assert.Contains("--vfs-cache-min-free-space 20Gi", line, StringComparison.Ordinal);
        Assert.Contains("--dir-cache-time 3600000s", line, StringComparison.Ordinal);
        Assert.Contains("--rc-addr 127.0.0.1:53682", line, StringComparison.Ordinal);
        Assert.DoesNotContain("--rc-user", line, StringComparison.Ordinal);
        Assert.DoesNotContain("--rc-pass", line, StringComparison.Ordinal);
        Assert.DoesNotContain("--rc-no-auth", line, StringComparison.Ordinal);
        Assert.DoesNotContain("--network-mode", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Mount_PassesTheUploadLimitOnlyWhenSet()
    {
        Assert.DoesNotContain("--bwlimit", MountPlan.Arguments(Settings()));
        var limited = MountPlan.Arguments(Settings() with { BandwidthLimitMiB = 5 }).ToList();
        Assert.Equal("5Mi", limited[limited.IndexOf("--bwlimit") + 1]);
        Assert.Equal("off", MountPlan.Rate(null));
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("letter")]
    [InlineData("cache-on-share")]
    [InlineData("cache-on-mount")]
    [InlineData("port")]
    [InlineData("fraction")]
    [InlineData("tiny-cache")]
    [InlineData("no-bandwidth")]
    [InlineData("huge-bandwidth")]
    public void Mount_RefusesUnsafeSettings(string change)
    {
        var bad = change switch
        {
            "remote" => Settings() with { Remote = "onedrive:folder" },
            "letter" => Settings() with { DriveLetter = 'C' },
            "cache-on-share" => Settings() with { CacheDirectory = @"\\192.0.2.10\Data\cache" },
            "cache-on-mount" => Settings() with { CacheDirectory = @"M:\cache" },
            "port" => Settings() with { ControlPort = 80 },
            "fraction" => Settings() with { WriteBack = TimeSpan.FromMilliseconds(1500) },
            "no-bandwidth" => Settings() with { BandwidthLimitMiB = 0 },
            "huge-bandwidth" => Settings() with { BandwidthLimitMiB = 10001 },
            _ => Settings() with { CacheMaxBytes = 1024 },
        };
        Assert.Throws<ArgumentException>(() => MountPlan.Arguments(bad));
    }

    [Theory]
    [InlineData(20L << 30, "20Gi")]
    [InlineData(1536L << 20, "1536Mi")]
    [InlineData(1_000_000_001L, "1000000001B")]
    public void Mount_AlwaysWritesSizeUnits(long bytes, string expected) => Assert.Equal(expected, MountPlan.Size(bytes));

    [Fact]
    public void Capacity_UsesTheBitrateFormulaAndReportsTheDeficit()
    {
        // 15 Mb/s for an hour is 6.75 GB, matching bitrate * seconds / 8000.
        var ok = CapacityAdmission.Evaluate(new CapacityInputs(300_000_000_000, 20L << 30, 15, TimeSpan.FromHours(1), 0));
        Assert.True(ok.Admitted);
        Assert.Equal(6_750_000_000, ok.ProjectedCaptureBytes);

        var tight = CapacityAdmission.Evaluate(new CapacityInputs(30_000_000_000, 20_000_000_000, 100, TimeSpan.FromHours(1), 1_000_000_000));
        Assert.False(tight.Admitted);
        Assert.Equal(45_000_000_000 + 1_000_000_000 + 20_000_000_000 - 30_000_000_000, tight.DeficitBytes);
        Assert.Throws<ArgumentException>(() => CapacityAdmission.Evaluate(new CapacityInputs(1, 1, 0, TimeSpan.FromHours(1), 0)));
    }
}

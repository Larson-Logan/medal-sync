using MedalSync.Storage;

namespace MedalSync.Tests;

/// <summary>Disk-space warnings while recording: thresholds, hysteresis, shared disks and failed probes.</summary>
public sealed class SpacePressureTests
{
    private const long GiB = 1L << 30;
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, VolumeInfo?> _volumes = new(StringComparer.Ordinal);
    private int _probes;

    private VolumeInfo? Describe(string path)
    {
        _probes++;
        return _volumes[path];
    }

    private static VolumeInfo Volume(string identity, long freeGiB) => new(identity + @":\", identity, VolumeKind.Fixed, "NTFS", freeGiB * GiB, 1000 * GiB);

    private SpacePressureMonitor Monitor(double? bitrate = null, params WatchedFolder[] folders) =>
        new(folders.Length == 0 ? [new("SSD cache", @"E:\Cache")] : folders, 40 * GiB, 20 * GiB, Describe, bitrate);

    [Theory]
    [InlineData(20, null, 40, 20)]
    [InlineData(0, null, 11, 1)]
    [InlineData(5, null, 15, 5)]
    [InlineData(20, 60, 60, 20)]
    public void Thresholds_FollowTheReserveUnlessConfigured(int reserve, int? configured, long warning, long critical)
    {
        var thresholds = SpacePressureMonitor.Thresholds(reserve, configured);
        Assert.Equal(warning * GiB, thresholds.Warning);
        Assert.Equal(critical * GiB, thresholds.Critical);
    }

    [Fact]
    public void Crossings_AreAnnouncedOnceWithHysteresisAndAQuietPeriod()
    {
        var monitor = Monitor();
        var now = Start;
        IReadOnlyList<string> At(long freeGiB)
        {
            _volumes[@"E:\Cache"] = Volume("E", freeGiB);
            var notices = monitor.Check(now);
            now += SpacePressureMonitor.CheckInterval;
            return notices;
        }

        Assert.Empty(At(100));
        var low = Assert.Single(At(35));
        Assert.StartsWith(@"Low disk space on E:\ (SSD cache): 37.6 GB free", low, StringComparison.Ordinal);
        Assert.Contains("never deletes", low, StringComparison.Ordinal);
        // Hovering around the warning level, even slightly above it, says nothing more.
        Assert.Empty(At(39));
        Assert.Empty(At(41));
        Assert.Empty(At(38));
        Assert.Contains("back to", Assert.Single(At(60)), StringComparison.Ordinal);
        // Dropping again soon after is not repeated within the quiet period, and so needs no recovery notice either.
        Assert.Empty(At(35));
        Assert.Empty(At(60));
        now += SpacePressureMonitor.QuietPeriod;
        Assert.Single(At(35));
    }

    [Fact]
    public void ReachingTheReserve_IsAlwaysAnnouncedAndRecoversThroughWarningQuietly()
    {
        var monitor = Monitor();
        var now = Start;
        IReadOnlyList<string> At(long freeGiB)
        {
            _volumes[@"E:\Cache"] = Volume("E", freeGiB);
            var notices = monitor.Check(now);
            now += SpacePressureMonitor.CheckInterval;
            return notices;
        }

        Assert.Single(At(35));
        var critical = Assert.Single(At(15));
        Assert.Contains("at the 21.5 GB reserve", critical, StringComparison.Ordinal);
        Assert.Empty(At(21));
        Assert.Empty(At(30));
        Assert.Empty(At(15));
        Assert.Contains("back to", Assert.Single(At(100)), StringComparison.Ordinal);
        // Straight from plenty to the reserve: said at once, even within the quiet period.
        Assert.Contains("reserve", Assert.Single(At(5)), StringComparison.Ordinal);
    }

    [Fact]
    public void FoldersOnOneDisk_ShareOneNoticeAndSeparateDisksEachGetTheirOwn()
    {
        var shared = Monitor(null, new("SSD cache", @"E:\Cache"), new("persistent clip folder", @"E:\Clips"));
        _volumes[@"E:\Cache"] = Volume("E", 30);
        _volumes[@"E:\Clips"] = Volume("E", 30);
        var notice = Assert.Single(shared.Check(Start));
        Assert.Contains("(SSD cache and persistent clip folder)", notice, StringComparison.Ordinal);

        var separate = Monitor(null, new("SSD cache", @"E:\Cache"), new("persistent clip folder", @"F:\Clips"));
        _volumes[@"F:\Clips"] = Volume("F", 30);
        Assert.Equal(2, separate.Check(Start).Count);
    }

    [Fact]
    public void ChecksRunOnlyEveryInterval()
    {
        var monitor = Monitor();
        _volumes[@"E:\Cache"] = Volume("E", 100);
        monitor.Check(Start);
        monitor.Check(Start + TimeSpan.FromSeconds(10));
        monitor.Check(Start + TimeSpan.FromSeconds(29));
        Assert.Equal(1, _probes);
        monitor.Check(Start + SpacePressureMonitor.CheckInterval);
        Assert.Equal(2, _probes);
    }

    [Fact]
    public void TimeLeft_IsAnApproximationFromTheBitrate()
    {
        var monitor = Monitor(bitrate: 80);
        _volumes[@"E:\Cache"] = Volume("E", 30);
        // 10 GiB above the reserve at 10 MB/s is about 18 minutes.
        Assert.Contains("roughly 18 minutes of recording (approximate)", Assert.Single(monitor.Check(Start)), StringComparison.Ordinal);
    }

    [Fact]
    public void FailedProbes_AreUnknownNotHealthyAndAreReportedOnce()
    {
        var monitor = Monitor();
        var now = Start;
        _volumes[@"E:\Cache"] = Volume("E", 30);
        Assert.Single(monitor.Check(now));
        _volumes[@"E:\Cache"] = null;
        for (int i = 1; i < SpacePressureMonitor.FailuresBeforeNotice; i++) Assert.Empty(monitor.Check(now += SpacePressureMonitor.CheckInterval));
        var unknown = Assert.Single(monitor.Check(now += SpacePressureMonitor.CheckInterval));
        Assert.Contains("could not be checked: its disk is not connected", unknown, StringComparison.Ordinal);
        Assert.Empty(monitor.Check(now += SpacePressureMonitor.CheckInterval));

        // A failed probe never counts as recovery; the warning stays until real space returns.
        _volumes[@"E:\Cache"] = Volume("E", 30);
        Assert.Empty(monitor.Check(now += SpacePressureMonitor.CheckInterval));
        _volumes[@"E:\Cache"] = Volume("E", 100);
        Assert.Contains("back to", Assert.Single(monitor.Check(now += SpacePressureMonitor.CheckInterval)), StringComparison.Ordinal);

        var throwing = new SpacePressureMonitor([new("SSD cache", @"E:\Cache")], 40 * GiB, 20 * GiB, _ => throw new IOException("synthetic"));
        IReadOnlyList<string> last = [];
        for (int i = 0; i < SpacePressureMonitor.FailuresBeforeNotice; i++) last = throwing.Check(Start + i * SpacePressureMonitor.CheckInterval);
        Assert.Contains("the disk could not be read", Assert.Single(last), StringComparison.Ordinal);
    }

    [Fact]
    public void ASlowDisk_DoesNotHoldUpTheCheckAndOnlyOneProbeRuns()
    {
        using var gate = new ManualResetEventSlim();
        int started = 0;
        var monitor = new SpacePressureMonitor([new("SSD cache", @"E:\Cache")], 40 * GiB, 20 * GiB, _ =>
        {
            Interlocked.Increment(ref started);
            gate.Wait(TimeSpan.FromSeconds(30));
            return Volume("E", 30);
        }, probeTimeout: TimeSpan.FromMilliseconds(20));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<string> last = [];
        for (int i = 0; i < SpacePressureMonitor.FailuresBeforeNotice; i++) last = monitor.Check(Start + i * SpacePressureMonitor.CheckInterval);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains("did not answer in time", Assert.Single(last), StringComparison.Ordinal);
        // Under a busy test run the probe can still be queued here; wait for it to start, then require exactly one.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref started) > 0, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Volatile.Read(ref started));

        // When the slow probe finally answers, its result is used.
        gate.Set();
        IReadOnlyList<string> answered = [];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        for (int i = 3; answered.Count == 0 && DateTime.UtcNow < deadline; i++)
            answered = monitor.Check(Start + i * SpacePressureMonitor.CheckInterval);
        Assert.StartsWith("Low disk space", Assert.Single(answered), StringComparison.Ordinal);
    }
}

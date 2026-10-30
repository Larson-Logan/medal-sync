using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class ReplicaRetryStatusTests
{
    [Fact]
    public void LocalLaunchCancelsRetryBeforeTakingTheGate()
    {
        if (!OperatingSystem.IsWindows()) return;
        string suffix = ".Test." + Guid.NewGuid().ToString("N");
        using var started = new ManualResetEventSlim();
        using var canceled = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                using var retry = ReplicaRetryInterlock.Retry(suffix);
                started.Set();
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    if (!OperatingSystem.IsWindows()) return false;
                    try { retry.RequireContinuing(); return false; }
                    catch (OperationCanceledException) { return true; }
                }, TimeSpan.FromSeconds(3)));
                canceled.Set();
            }
            catch (Exception ex) { failure = ex; started.Set(); }
        });
        thread.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        using (ReplicaRetryInterlock.Launch(suffix)) Assert.True(canceled.IsSet);
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
        Assert.Null(failure);
        using var again = ReplicaRetryInterlock.Retry(suffix);
        again.RequireContinuing();
    }

    [Fact]
    public void RetryStatusReportsProgressResultAndStalenessWithoutChangingReady()
    {
        using var fixture = new MedalFixture();
        var state = new LocalState(fixture.PathFor("state"));
        const string library = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var now = DateTimeOffset.UtcNow;
        using (var telemetry = new ReplicaRetryStatus(state, library, 12))
        {
            telemetry.Start();
            telemetry.Report(new(SessionPhase.Uploading, "Copying selected clips.", Replica: "cloud"));
            var status = DesktopStatus.Read(state, library, Ownership.Initial(library, "12345", now), null, false, now);
            Assert.Equal(SessionPhase.Ready, status.Phase);
            Assert.Contains("Copying selected clips", status.Notice);
            Assert.Equal("cloud", status.Replica);
            Assert.True(ReplicaRetryStatus.Read(state, library, now)!.Running);
            telemetry.Complete("Selected copies verified.");
            Assert.False(ReplicaRetryStatus.Read(state, library, now)!.Running);
        }
        Assert.Null(ReplicaRetryStatus.Read(state, new string('b', 32), now));
        string path = Path.Combine(state.Root, "replication", "retry-status.json");
        AtomicFile.Replace(path, Json.Serialize(new ReplicaRetryReport(1, library, 12, true, "Working", now.AddMinutes(-1))));
        var stale = ReplicaRetryStatus.Read(state, library, now)!;
        Assert.False(stale.Running);
        Assert.Contains("stale", stale.Message);
    }
}

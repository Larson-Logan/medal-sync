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
    public void CloudRetryButtonNeedsReadyPendingCopiesAndNoLiveRetry()
    {
        using var fixture = new MedalFixture();
        var state = new LocalState(fixture.PathFor("state"));
        string library = new('a', 32);
        var now = DateTimeOffset.UtcNow;
        var journal = Ownership.Initial(library, "12345", now);
        DesktopStatus Read() => DesktopStatus.Read(state, library, journal, null, false, DateTimeOffset.UtcNow);
        Assert.False(Read().CanRetryCloudCopies);
        StorageReplication.MarkPending(state, library, "cloud", now);
        Assert.True(Read().CanRetryCloudCopies);
        Assert.Contains("Choose Retry cloud copies", Read().Notice, StringComparison.Ordinal);
        using (var telemetry = new ReplicaRetryStatus(state, library, 0))
        {
            telemetry.Start();
            Assert.False(Read().CanRetryCloudCopies);
            telemetry.Complete("Still pending.");
            Assert.True(Read().CanRetryCloudCopies);
        }
        var owner = new DeviceIdentity(new string('b', 32), "Other PC");
        journal = Ownership.Acquire(journal, owner, SessionCoordinator.NewId(), now);
        Assert.False(Read().CanRetryCloudCopies);
        Assert.False(DesktopStatus.Read(state, new string('c', 32), Ownership.Initial(new string('c', 32), "12345", now),
            null, false, now).CanRetryCloudCopies);
        Assert.False((new DesktopStatus(SessionPhase.Ready, "Ready", now, CanRetryCloudCopies: true)).At(now.AddMinutes(1)).CanRetryCloudCopies);
    }

    [Theory]
    [InlineData(0, "Cloud copies verified.")]
    [InlineData(3, "canceled or still pending")]
    [InlineData(1, "needs action. Sign in again.")]
    public void CloudRetryExitCodesDistinguishSuccessCancellationAndAction(int exitCode, string expected)
    {
        var result = new CloudRetryCommandResult(exitCode, "Earlier progress", "Sign in again.");
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
        if (exitCode == 3) Assert.DoesNotContain("needs action", result.Message, StringComparison.Ordinal);
    }

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

using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class DesktopStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(10);
    private static readonly DeviceIdentity Me = new(new string('a', 32), "Synthetic PC");
    private static readonly string Session = new('b', 32);
    private static OwnershipRecord Journal => Ownership.MarkActive(
        Ownership.Acquire(Ownership.Initial(new string('c', 32), "12345", Now), Me, Session, Now), Me, Session, Now);
    private static ProgressEnvelope Report(SessionPhase phase) => new(1, Journal.LibraryId, Journal.AccountId,
        Me.DeviceId, Now, new(phase, "Synthetic next action", Session, 3, 4096, "cloud-backup"));

    [Theory]
    [InlineData((int)SessionPhase.Starting)]
    [InlineData((int)SessionPhase.Active)]
    [InlineData((int)SessionPhase.Uploading)]
    [InlineData((int)SessionPhase.Verifying)]
    [InlineData((int)SessionPhase.RecoveryNeeded)]
    [InlineData((int)SessionPhase.Unknown)]
    public void FakeSupervisorReportsDistinctStatesAndAvailableProgress(int value)
    {
        var phase = (SessionPhase)value;
        var status = DesktopStatus.Resolve(Journal, Me, Report(phase), true, Now);
        Assert.Equal(phase, status.Phase);
        Assert.Equal(3, status.QueuedFiles);
        Assert.Equal(4096, status.QueuedBytes);
        Assert.Equal("cloud-backup", status.Replica);
        Assert.Equal(Now, status.ConfirmedAt);
        Assert.False(status.CanRetry);
    }

    [Fact]
    public void AnEmptyQueueOrAFalseReadyReportCannotReleaseAnOwnedSession()
    {
        var report = Report(SessionPhase.Ready) with { Progress = new(SessionPhase.Ready, "wrong", Session, 0, 0) };
        Assert.Equal(SessionPhase.Unknown, DesktopStatus.Resolve(Journal, Me, report, true, Now).Phase);
        Assert.Equal(SessionPhase.RecoveryNeeded, DesktopStatus.Resolve(Journal, Me, report, false, Now).Phase);
        Assert.Equal(SessionPhase.Ready, DesktopStatus.Resolve(Ownership.Initial(Journal.LibraryId, Journal.AccountId, Now), Me, report, false, Now).Phase);
    }

    [Fact]
    public void DisconnectAndReconnectPreserveLastConfirmationAndNeverOfferAnUnknownRetry()
    {
        var connected = DesktopStatus.Resolve(Journal, Me, Report(SessionPhase.Uploading), true, Now);
        var stale = connected.At(Now.AddSeconds(21));
        Assert.Equal(SessionPhase.Unknown, stale.Phase);
        Assert.Equal(Now, stale.ConfirmedAt);
        Assert.Null(stale.QueuedFiles);
        Assert.False(stale.CanRetry);
        var restored = DesktopStatus.Resolve(Journal, Me, Report(SessionPhase.Verifying) with { ConfirmedAt = Now.AddMinutes(1) }, true, Now.AddMinutes(1));
        Assert.Equal(SessionPhase.Verifying, restored.Phase);
        Assert.Equal(Now.AddMinutes(1), restored.ConfirmedAt);
    }

    [Fact]
    public void StaleForeignFutureAndWrongSessionReportsAreUnknown()
    {
        foreach (var report in new[]
        {
            Report(SessionPhase.Active) with { ConfirmedAt = Now.AddSeconds(-21) },
            Report(SessionPhase.Active) with { ConfirmedAt = Now.AddSeconds(10) },
            Report(SessionPhase.Active) with { LibraryId = new string('d', 32) },
            Report(SessionPhase.Active) with { AccountId = "54321" },
            Report(SessionPhase.Active) with { DeviceId = new string('e', 32) },
            Report(SessionPhase.Active) with { Progress = new(SessionPhase.Active, "wrong session", new string('f', 32)) },
        }) Assert.Equal(SessionPhase.Unknown, DesktopStatus.Resolve(Journal, Me, report, true, Now).Phase);
        Assert.Equal(SessionPhase.Unknown, DesktopStatus.Resolve(Journal, Me, null, true, Now).Phase);
    }

    [Fact]
    public void WaitingAndRecoveryCannotRetryAnotherComputersSession()
    {
        var other = new DeviceIdentity(new string('d', 32), "Other PC");
        var waiting = DesktopStatus.Resolve(Journal, other, null, false, Now);
        Assert.Equal(SessionPhase.Waiting, waiting.Phase);
        Assert.Contains(Me.DisplayName, waiting.NextAction, StringComparison.Ordinal);
        Assert.False(waiting.CanRetry);
        var recovery = DesktopStatus.Resolve(Journal with { State = OwnershipState.RecoveryRequired }, other, null, false, Now);
        Assert.Equal(SessionPhase.RecoveryNeeded, recovery.Phase);
        Assert.False(recovery.CanRetry);
        Assert.True(DesktopStatus.Resolve(Journal, Me, null, false, Now).CanRetry);
    }

    [Fact]
    public void StartupDoesNotFlashReadyBeforeAcquiringOwnership()
    {
        var clean = Ownership.Initial(Journal.LibraryId, Journal.AccountId, Now);
        Assert.Equal(SessionPhase.Starting, DesktopStatus.Resolve(clean, Me, Report(SessionPhase.Starting), true, Now).Phase);
        Assert.Equal(SessionPhase.Unknown, DesktopStatus.Resolve(clean, Me, null, true, Now).Phase);
        var interrupted = DesktopStatus.Resolve(Journal with { State = OwnershipState.Starting }, Me, null, false, Now);
        Assert.Equal(SessionPhase.RecoveryNeeded, interrupted.Phase);
        Assert.False(interrupted.CanRetry);
        Assert.Contains("startup cleanup", interrupted.NextAction, StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherPreservesSessionAcrossReplicaReportsAndUsesOnlyPrivateTelemetry()
    {
        using var fixture = new MedalFixture();
        var state = new LocalState(fixture.PathFor("private-state"));
        Directory.CreateDirectory(state.Root);
        using (var publisher = new DesktopStatusPublisher(state, Journal.LibraryId, Journal.AccountId, Me.DeviceId))
        {
            publisher.Report(new(SessionPhase.Verifying, "verify", Session));
            publisher.Report(new(SessionPhase.Uploading, "copy", Replica: "cloud-backup"));
            var report = Json.ReadFile<ProgressEnvelope>(Path.Combine(state.Root, "desktop-status.json"), "status");
            Assert.Equal(Session, report.Progress.SessionId);
            Assert.Equal("cloud-backup", report.Progress.Replica);
            Assert.Null(report.Progress.QueuedBytes);
        }
        Assert.Equal(new[] { "desktop-status.json" }, Directory.GetFiles(state.Root).Select(Path.GetFileName));
        // An unavailable telemetry directory must also be harmless.
        using var unavailable = new DesktopStatusPublisher(new LocalState(fixture.PathFor("missing")), Journal.LibraryId, Journal.AccountId, Me.DeviceId);
        unavailable.Report(new(SessionPhase.Ready, "done"));
    }
}

using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Session;
using MedalSync.Storage;

namespace MedalSync.Tests;

/// <summary>The PC stays awake only while it holds work that blocks a handoff, and every outcome releases it.</summary>
public sealed partial class SessionTests
{
    private sealed class FakeKeepAwake : IKeepAwake
    {
        public int Held { get; private set; }

        /// <summary>How often Windows would have been asked: each time the holds went from none to one.</summary>
        public int Requests { get; private set; }

        public bool Refuse { get; set; }

        public IDisposable Hold()
        {
            if (Refuse) throw new InvalidOperationException("synthetic power request failure");
            if (Held++ == 0) Requests++;
            return new KeepAwakeHold(() => Held--);
        }
    }

    private (Pc Pc, string Session, FakeKeepAwake Awake) FinishingPc(Func<bool>? isIdle = null,
        Func<LibrarySnapshot, Action, bool>? idleSync = null, FakeKeepAwake? awake = null)
    {
        awake ??= new FakeKeepAwake();
        var pc = Computer(FirstPc, "first", keepAwake: awake, isIdle: isIdle, idleSync: idleSync);
        AssertStarted(pc.Coordinator.Launch());
        Assert.Equal(0, awake.Held);
        return (pc, _store.ReadJournal().SessionId!, awake);
    }

    [Fact]
    public void FinishHoldsTheWakeRequestWhileUploadingAndReleasesItWhenReady()
    {
        var (pc, session, awake) = FinishingPc();
        Record(pc, "new-recording");
        pc.Medal.Running = false;
        var heldDuringDrain = new List<int>();
        pc.Mount.OnDrain = () => heldDuringDrain.Add(awake.Held);
        _assets.OnServerCheck = () => Assert.Equal(1, awake.Held);

        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);

        Assert.NotEmpty(heldDuringDrain);
        Assert.All(heldDuringDrain, held => Assert.Equal(1, held));
        Assert.Equal(0, awake.Held);
    }

    [Fact]
    public void FinishReleasesTheWakeRequestWhenBlocked()
    {
        var (pc, session, awake) = FinishingPc();
        pc.Medal.Running = false;
        pc.Mount.Steady = new(DrainState.Failed, ["synthetic upload failure"], 1, 10);

        Assert.Equal(FinishStatus.Blocked, pc.Coordinator.Finish(session).Status);

        Assert.Equal(1, awake.Requests);
        Assert.Equal(0, awake.Held);
    }

    [Fact]
    public void FinishReleasesTheWakeRequestWhileWaitingForStorage()
    {
        var (pc, session, awake) = FinishingPc();
        pc.Medal.Running = false;
        pc.Mount.OnDrain = () => throw new StorageOfflineException("synthetic outage");

        var outcome = pc.Coordinator.Finish(session);

        Assert.Equal(PendingCause.StorageOffline, outcome.Cause);
        Assert.Equal(0, awake.Held);
    }

    [Fact]
    public void FinishReleasesTheWakeRequestWhenAnUnexpectedErrorEscapes()
    {
        var (pc, session, awake) = FinishingPc();
        pc.Medal.Running = false;
        pc.Mount.OnDrain = () => throw new InvalidOperationException("synthetic bug");

        Assert.Throws<InvalidOperationException>(() => pc.Coordinator.Finish(session));

        Assert.Equal(0, awake.Held);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
    }

    [Fact]
    public void AFailedWakeRequestNeverChangesTheFinish()
    {
        var (pc, session, _) = FinishingPc(awake: new FakeKeepAwake { Refuse = true });
        Record(pc, "new-recording");
        pc.Medal.Running = false;

        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void SupervisorKeepsOneWakeRequestAcrossFinishAttemptsWhileUploadsRun()
    {
        var (pc, session, awake) = FinishingPc();
        Record(pc, "new-recording");
        pc.Medal.Running = false;
        // Still uploading when the first attempt's drain timeout ends, so the supervisor starts a second attempt.
        int drains = 0;
        pc.Mount.Steady = new(DrainState.Pending, ["synthetic slow upload"], 1, 4096);
        pc.Mount.OnDrain = () =>
        {
            if (++drains == 40) pc.Mount.Steady = new(DrainState.Drained, [], 0, 0);
        };
        var heldWhilePaused = new List<int>();
        _clock.OnSleep = () => heldWhilePaused.Add(awake.Held);

        var outcome = pc.Coordinator.Supervise(session, _ => { }, CancellationToken.None);

        Assert.Equal(FinishStatus.Published, outcome.Status);
        Assert.True(drains >= 40);
        Assert.NotEmpty(heldWhilePaused);
        Assert.All(heldWhilePaused, held => Assert.True(held > 0));
        Assert.Equal(1, awake.Requests);
        Assert.Equal(0, awake.Held);
    }

    [Fact]
    public void SupervisorLetsThePcSleepWhileItWaitsForStorage()
    {
        var (pc, session, awake) = FinishingPc();
        pc.Medal.Running = false;
        pc.Mount.OnDrain = () => throw new StorageOfflineException("synthetic outage");
        using var cancel = new CancellationTokenSource();
        var heldWhileWaiting = new List<int>();
        _clock.OnSleep = () =>
        {
            heldWhileWaiting.Add(awake.Held);
            if (heldWhileWaiting.Count == 5) cancel.Cancel();
        };

        var outcome = pc.Coordinator.Supervise(session, _ => { }, cancel.Token);

        Assert.Equal(FinishStatus.Pending, outcome.Status);
        Assert.Equal(5, heldWhileWaiting.Count);
        Assert.All(heldWhileWaiting, held => Assert.Equal(0, held));
        Assert.Equal(0, awake.Held);
        Assert.Equal(FirstPc, _store.ReadJournal().Owner);
    }

    [Fact]
    public void SupervisorReleasesTheWakeRequestWhenMedalReopens()
    {
        var (pc, session, awake) = FinishingPc();
        pc.Medal.Running = false;
        pc.Mount.Steady = new(DrainState.Pending, ["synthetic slow upload"], 1, 4096);
        pc.Mount.OnDrain = () => pc.Medal.Running = true;
        using var cancel = new CancellationTokenSource();
        var heldWhileOpen = new List<int>();
        _clock.OnSleep = () =>
        {
            if (_store.ReadJournal().State != OwnershipState.Active || cancel.IsCancellationRequested) return;
            heldWhileOpen.Add(awake.Held);
            cancel.Cancel();
        };

        pc.Coordinator.Supervise(session, _ => { }, cancel.Token);

        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
        Assert.Equal(0, awake.Held);
        Assert.Equal([0], heldWhileOpen);
    }

    [Fact]
    public void IdleSyncHoldsTheWakeRequestOnlyWhileItTransfers()
    {
        var awake = new FakeKeepAwake();
        var heldDuringSync = new List<int>();
        var (pc, session, _) = FinishingPc(isIdle: () => true, idleSync: (_, guard) =>
        {
            guard();
            heldDuringSync.Add(awake.Held);
            return true;
        }, awake: awake);
        Record(pc, "clip-2");
        using var cancel = new CancellationTokenSource();
        var heldAfterSync = new List<int>();
        _clock.OnSleep = () =>
        {
            heldAfterSync.Add(awake.Held);
            cancel.Cancel();
        };

        pc.Coordinator.Supervise(session, _ => { }, cancel.Token);

        Assert.Equal([1], heldDuringSync);
        Assert.Equal([0], heldAfterSync);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }
}

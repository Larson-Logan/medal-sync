using MedalSync.Coordination;
using MedalSync.Session;
using MedalSync.Storage;

namespace MedalSync.Tests;

/// <summary>The supervisor and finish release held uploads; a failed release never changes ownership or verification.</summary>
public sealed partial class SessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SupervisorHoldsOnlyTheReplayBufferWhileAGameRecords(bool recording)
    {
        var pc = Computer(FirstPc, "first");
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Recording = () => recording;
        using var cancel = new CancellationTokenSource();
        _clock.OnSleep = cancel.Cancel;

        pc.Coordinator.Supervise(session, _ => { }, cancel.Token);

        Assert.Equal([!recording], pc.Mount.Releases);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }

    [Fact]
    public void FinishReleasesEverythingBeforeEachDrainCheck()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "new-recording");
        pc.Medal.Running = false;
        int releasesAtDrain = -1;
        pc.Mount.Verdicts.Enqueue(new(DrainState.Pending, [], 1, 10));
        pc.Mount.OnDrain = () => releasesAtDrain = pc.Mount.Releases.Count;

        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);

        Assert.True(pc.Mount.Releases.Count >= 2);
        Assert.All(pc.Mount.Releases, includeBuffer => Assert.True(includeBuffer));
        Assert.Equal(pc.Mount.Releases.Count, releasesAtDrain);
    }

    [Fact]
    public void AFailedReleaseIsANoticeWhileRecordingAndNeverStopsAFinish()
    {
        var notices = new List<string>();
        var pc = Computer(FirstPc, "first");
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Recording = () => true;
        pc.Mount.ReleaseProblem = "synthetic release problem";
        int polls = 0;
        _clock.OnSleep = () =>
        {
            if (++polls == 2) pc.Mount.ReleaseThrows = true;
            if (polls == 3) { Record(pc, "new-recording"); pc.Medal.Running = false; }
        };

        var outcome = pc.Coordinator.Supervise(session, notices.Add, CancellationToken.None);

        Assert.Equal(FinishStatus.Published, outcome.Status);
        Assert.Single(notices, n => n == "synthetic release problem");
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }
}

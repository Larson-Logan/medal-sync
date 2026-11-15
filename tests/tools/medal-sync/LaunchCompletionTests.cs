using MedalSync.Coordination;
using MedalSync.Session;

namespace MedalSync.Tests;

/// <summary>Storage failures after the session is prepared, at the last two steps before Medal opens (server mode).</summary>
public sealed partial class SessionTests
{
    /// <summary>
    /// Replaces journal reads so that, once the returned action arms it, read number n fails when <paramref name="fail"/>(n) is true.
    /// The returned disarm action restores ordinary reads.
    /// </summary>
    private (Action Arm, Action Disarm) JournalReadFailures(Func<int, bool> fail)
    {
        var normal = _store.JournalReader;
        int reads = 0;
        bool armed = false;
        _store.JournalReader = new FreshCoordinationRead
        {
            ReadOpen = path => armed && fail(++reads) ? throw new IOException("Synthetic share drop.") : FreshCoordinationRead.Default.ReadOpen(path),
            Sleep = _ => { },
            PauseDirectory = _fixture.PathFor("pauses"),
        };
        return (() => armed = true, () => { armed = false; _store.JournalReader = normal; });
    }

    [Fact]
    public void Launch_GivesTheStartBackWhenMarkingTheSessionActiveFails()
    {
        // The read before the Active write fails; cleanup then reads normally.
        var (arm, disarm) = JournalReadFailures(n => n == 1);
        var pc = Computer(FirstPc, "first", prepareNativeBackup: () => arm());

        var outcome = pc.Coordinator.Launch();
        disarm();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("Synthetic share drop.", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stay on this PC", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, pc.Medal.Launches);
        Assert.False(pc.Mount.IsRunning);
        var journal = _store.ReadJournal();
        Assert.Equal(OwnershipState.Clean, journal.State);
        Assert.Null(journal.Owner);
        Assert.Equal(pc.Mount.Starts, pc.Mount.RetainedAbortedStarts);
    }

    [Fact]
    public void Launch_KeepsOwnershipAndCacheWhenCleanupAfterAFailedActiveWriteAlsoFails()
    {
        var (arm, disarm) = JournalReadFailures(_ => true);
        var pc = Computer(FirstPc, "first", prepareNativeBackup: () => arm());

        var outcome = pc.Coordinator.Launch();
        disarm();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("stay on this PC", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, pc.Medal.Launches);
        Assert.False(pc.Mount.IsRunning);
        var journal = _store.ReadJournal();
        Assert.Equal(OwnershipState.Starting, journal.State);
        Assert.Equal(FirstPc, journal.Owner);
        Assert.Empty(pc.Mount.RetainedAbortedStarts);
        Assert.Empty(pc.Mount.Forgotten);

        // The next launch gives the unfinished start back and starts normally.
        var retried = Computer(FirstPc, "first", database: pc.Database).Coordinator.Launch();
        AssertStarted(retried);
    }

    [Fact]
    public void Launch_KeepsTheSessionWhenTheActiveWriteLandedButItsReadBackFailed()
    {
        // The Active write is applied; only its read-back fails, so the start cannot be given back.
        var (arm, disarm) = JournalReadFailures(n => n == 2);
        var pc = Computer(FirstPc, "first", prepareNativeBackup: () => arm());

        var outcome = pc.Coordinator.Launch();
        disarm();

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("stay on this PC", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, pc.Medal.Launches);
        Assert.False(pc.Mount.IsRunning);
        var journal = _store.ReadJournal();
        Assert.Equal(OwnershipState.Active, journal.State);
        Assert.Equal(FirstPc, journal.Owner);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void Launch_StartsMedalWhenItsOwnSwitchRequestCannotBeRemoved()
    {
        _store.WriteRequest(new HandoffRequest(HandoffRequest.CurrentFormat, FirstPc, new string('9', 32), _clock.Now));
        string request = Path.Combine(_store.Root, "requests", FirstPc.DeviceId + ".json");
        Assert.True(File.Exists(request));
        var pc = Computer(FirstPc, "first");

        LaunchOutcome outcome;
        // Without FileShare.Delete the delete fails, as a share that drops at this moment would.
        using (new FileStream(request, FileMode.Open, FileAccess.Read, FileShare.Read))
            outcome = pc.Coordinator.Launch();

        AssertStarted(outcome);
        Assert.Equal(1, pc.Medal.Launches);
        Assert.True(pc.Mount.IsRunning);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
        Assert.True(File.Exists(request));
    }
}

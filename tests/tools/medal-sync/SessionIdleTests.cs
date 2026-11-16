using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

public sealed partial class SessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleCheckpointLeavesOwnershipActiveAndQuitOrShutdownReusesItInSeconds(bool shuttingDown)
    {
        var shutdown = new SessionShutdown(_clock);
        List<SessionProgress> reports = [];
        int syncs = 0;
        var pc = Computer(FirstPc, "first", shutdown: shutdown, isIdle: () => true, progress: reports.Add,
            idleSync: (_, guard) => { guard(); syncs++; return true; });
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        var original = _store.ReadJournal();
        DateTimeOffset finishAt = default;
        byte[]? checkpoint = null;
        _clock.OnSleep = () =>
        {
            Assert.Equal(original, _store.ReadJournal());
            checkpoint = File.ReadAllBytes(Path.Combine(_store.Generations.Directory, GenerationStore.Name(1), "manifest.json"));
            Assert.Equal(LaunchStatus.Waiting, Computer(SecondPc, "second").Coordinator.Launch().Status);
            Assert.Empty(pc.Mount.Forgotten);
            finishAt = _clock.Now;
            pc.Medal.Running = false;
            if (shuttingDown) shutdown.RequestEnd();
        };
        var result = pc.Coordinator.Supervise(session, _ => { }, CancellationToken.None);
        Assert.Equal(FinishStatus.Published, result.Status);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, syncs);
        Assert.True(_clock.Now - finishAt < TimeSpan.FromSeconds(2));
        Assert.True(_clock.Now - finishAt < shutdown.Budget);
        Assert.Equal(checkpoint, File.ReadAllBytes(Path.Combine(_store.Generations.Directory, GenerationStore.Name(1), "manifest.json")));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Contains(reports, r => r.ClipsSynced && r.Phase == SessionPhase.Active);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BusyOrRecordingPcDoesNotStartIdleSync(bool recording)
    {
        var pc = Computer(FirstPc, "first", isIdle: () => recording, idleSync: (_, _) => throw new InvalidOperationException("Must stay idle."));
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Recording = () => recording;
        using var cancel = new CancellationTokenSource();
        _clock.OnSleep = cancel.Cancel;
        pc.Coordinator.Supervise(session, _ => { }, cancel.Token);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
        Assert.Equal(new long[] { 0 }, _store.Generations.Numbered());
    }

    [Fact]
    public void StorageOutageDuringIdleSyncRetainsOwnershipAndCacheAndRetries()
    {
        int attempts = 0;
        var pc = Computer(FirstPc, "first", isIdle: () => true, idleSync: (_, _) =>
        {
            if (++attempts == 1) throw new IOException("synthetic storage outage");
            return true;
        });
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        _clock.OnSleep = () =>
        {
            Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
            Assert.Empty(pc.Mount.Forgotten);
            if (attempts == 2) pc.Medal.Running = false;
        };
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Supervise(session, _ => { }, CancellationToken.None).Status);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void FinishPublishesLaterMetadataInsteadOfReusingAnOutdatedCheckpoint()
    {
        int passes = 0;
        var pc = Computer(FirstPc, "first", isIdle: () => true, idleSync: (_, _) => { passes++; return true; });
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        _clock.OnSleep = () =>
        {
            Record(pc, "clip-3");
            pc.Medal.Running = false;
        };
        var result = pc.Coordinator.Supervise(session, _ => { }, CancellationToken.None);
        Assert.Equal(FinishStatus.Published, result.Status);
        Assert.Equal(2, result.Generation);
        Assert.Equal(1, passes);
        Assert.Equal(3, _store.Generations.Read(2).Snapshot.RowCount("contents"));
    }

    [Fact]
    public void IdleSyncNeverCheckpointsAnUnreviewedRemoval()
    {
        List<SessionProgress> reports = [];
        var pc = Computer(FirstPc, "first", isIdle: () => true, progress: reports.Add, idleSync: (_, _) => true);
        string session = pc.Coordinator.Launch().Session!;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id = 'clip-1'");
        using var cancel = new CancellationTokenSource();
        _clock.OnSleep = cancel.Cancel;
        pc.Coordinator.Supervise(session, _ => { }, cancel.Token);
        Assert.Equal(new long[] { 0 }, _store.Generations.Numbered());
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
        Assert.DoesNotContain(reports, p => p.ClipsSynced);
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Blocked, pc.Coordinator.Finish(session).Status);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void NewClipClearsSyncedStatusWithoutReleasingOwnership()
    {
        List<SessionProgress> reports = [];
        int passes = 0;
        var pc = Computer(FirstPc, "first", isIdle: () => true, progress: reports.Add,
            idleSync: (_, _) => { passes++; return true; });
        string session = pc.Coordinator.Launch().Session!;
        using var cancel = new CancellationTokenSource();
        int sleeps = 0;
        _clock.OnSleep = () =>
        {
            if (++sleeps == 1) Record(pc, "clip-2");
            else cancel.Cancel();
        };
        pc.Coordinator.Supervise(session, _ => { }, cancel.Token);
        Assert.Contains(reports, r => r.ClipsSynced);
        Assert.False(reports[^1].ClipsSynced);
        Assert.Equal(1, passes);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }

    [Fact]
    public void OutageAfterCheckpointStillPreventsReleaseAndCacheCleanup()
    {
        var pc = Computer(FirstPc, "first", isIdle: () => true, idleSync: (_, _) => true);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        using var cancel = new CancellationTokenSource();
        _clock.OnSleep = cancel.Cancel;
        pc.Coordinator.Supervise(session, _ => { }, cancel.Token);
        _assets.ServerReachable = false;
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Finish(session).Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingOrOwnershipChangeDuringIdleCannotStageOrCommit(bool abandon)
    {
        Pc? pc = null;
        pc = Computer(FirstPc, "first", isIdle: () => true, idleSync: (_, guard) =>
        {
            if (abandon)
                _store.Update(r => Ownership.Abandon(r, SecondPc, r.SessionId!, "synthetic explicit recovery", _clock.Now), TimeSpan.FromSeconds(1));
            else pc!.Medal.Recording = () => true;
            guard();
            return true;
        });
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        using var cancel = new CancellationTokenSource();
        _clock.OnSleep = cancel.Cancel;
        pc.Coordinator.Supervise(session, _ => { }, cancel.Token);
        Assert.Equal(new long[] { 0 }, _store.Generations.Numbered());
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void ShutdownInterruptsAnIdleTransferBeforeFinishStarts()
    {
        var shutdown = new SessionShutdown(_clock);
        Pc? pc = null;
        pc = Computer(FirstPc, "first", shutdown: shutdown, isIdle: () => true, idleSync: (_, guard) =>
        {
            shutdown.RequestEnd();
            pc!.Medal.Running = false;
            guard();
            Assert.Fail("Shutdown must interrupt idle work.");
            return false;
        });
        string session = pc.Coordinator.Launch().Session!;
        var started = _clock.Now;
        Assert.Equal(FinishStatus.Unchanged, pc.Coordinator.Supervise(session, _ => { }, CancellationToken.None).Status);
        Assert.True(_clock.Now - started < shutdown.Budget);
    }
}

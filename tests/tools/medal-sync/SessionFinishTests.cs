using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>Finish: drain, review, verification, publication and release.</summary>
public sealed partial class SessionTests
{
    [Fact]
    public void IncompatibleUpdateStillDrainsButRetainsOwnershipAndCacheUntilRetry()
    {
        var compatibility = new FakeCompatibility();
        var pc = Computer(FirstPc, "first", compatibility: compatibility);
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "after-update");
        pc.Medal.Running = false;
        compatibility.Supported = false;
        int drains = 0;
        pc.Mount.OnDrain = () => drains++;

        var blocked = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, blocked.Status);
        Assert.Equal(1, drains);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(FirstPc, _store.ReadJournal().Owner);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);

        compatibility.Supported = true;
        compatibility.CurrentVersions = Versions with { App = "2644.591.1" };
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(compatibility.CurrentVersions, _store.Generations.Read(1).Manifest.Medal);
        Assert.Contains(session, pc.Mount.Forgotten);
    }

    [Fact]
    public void UpdateDuringFinalVerificationRetainsSessionAndCanRetryWithCurrentVersions()
    {
        var compatibility = new FakeCompatibility();
        var pc = Computer(FirstPc, "first", compatibility: compatibility);
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "after-update");
        pc.Medal.Running = false;
        _assets.OnServerCheck = () => compatibility.CurrentVersions = Versions with { App = "2644.591.1" };

        var blocked = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, blocked.Status);
        Assert.Contains("updated while the handoff", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(compatibility.CurrentVersions, _store.Generations.Read(1).Manifest.Medal);
    }

    [Fact]
    public void ExplicitFinishRecoversStartingAfterRestartWithoutLaunchingOrDeletingCache()
    {
        var (state, config) = RecoverySetup();
        string session = SessionCoordinator.NewId();
        _store.Update(r => Ownership.Acquire(r, FirstPc, session, _clock.Now), TimeSpan.FromSeconds(1));
        var secret = state.SessionSecret(session, _clock.Now);
        Directory.CreateDirectory(config.SessionCacheDirectory(session));
        File.WriteAllText(Path.Combine(config.SessionCacheDirectory(session), "unknown"), "retain");
        // Simulate a crash after evidence was retained but before journal publication completed.
        state.RetainAbortedStart(session, LibraryId, config.SessionCacheDirectory(session), _clock.Now);
        Assert.Equal(secret, new LocalState(state.Root).SessionSecret(session, _clock.Now));
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(state.Root, false, false, _ => _store.ReadJournal()));
        var restarted = Computer(FirstPc, "restarted");
        restarted.Mount.OnRetainAbortedStart = id => state.RetainAbortedStart(id, LibraryId, config.SessionCacheDirectory(id), _clock.Now);
        Assert.Equal(FinishStatus.Unchanged, restarted.Coordinator.Finish(session).Status);
        Assert.Equal(0, restarted.Medal.Launches);
        Assert.Empty(restarted.Mount.Starts);
        Assert.Empty(restarted.Mount.Forgotten);
        Assert.Equal("retain", File.ReadAllText(Path.Combine(config.SessionCacheDirectory(session), "unknown")));
        SetupMaintenance.RequireReady(state.Root, false, false, _ => _store.ReadJournal());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FinalVerificationMutationRetainsOwnershipAndCacheUntilRetry(bool changedBeforeFinish, bool duringStop)
    {
        var pc = Computer(FirstPc, "first");
        string session = pc.Coordinator.Launch().Session!;
        if (changedBeforeFinish) Record(pc, "early");
        pc.Medal.Running = false;
        Action mutate = () => Record(pc, "late");
        if (duringStop) pc.Mount.OnStop = mutate;
        else _assets.OnServerCheck = mutate;

        var blocked = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, blocked.Status);
        Assert.Contains("library changed", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(session, _store.ReadJournal().SessionId);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.False(pc.Mount.IsRunning);
        Assert.Single(_store.Generations.Numbered());
        Assert.Equal(LaunchStatus.Waiting, Computer(SecondPc, "second").Coordinator.Launch().Status);

        pc.Mount.OnStop = null;
        _assets.OnServerCheck = null;
        var retried = pc.Coordinator.Finish(session);
        Assert.Equal(FinishStatus.Published, retried.Status);
        Assert.Equal(changedBeforeFinish ? 3 : 2, _store.Generations.Read(retried.Generation).Snapshot.RowCount("contents"));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Contains(session, pc.Mount.Forgotten);
    }

    [Fact]
    public void DeletingTheLastClipHandsOffAnEmptyGenerationWithBackupAndDeviceSettings()
    {
        var first = Computer(FirstPc, "first");
        var second = Computer(SecondPc, "second");
        AssertStarted(second.Coordinator.Launch());
        second.Medal.Running = false;
        Assert.Equal(FinishStatus.Unchanged, second.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        string secondSettings = MedalFixture.Settings(second.Database);
        var old = LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId);
        string[] earlierBackups = Directory.GetFiles(_fixture.PathFor("second", "backups"), "*.db");

        AssertStarted(first.Coordinator.Launch());
        MedalFixture.DeleteContent(first.Database, "clip-1");
        first.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, first.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        var empty = _store.Generations.Read(1).Snapshot;
        Assert.Equal(0, empty.RowCount("contents"));
        Assert.Equal(1, empty.RowCount("deleted_contents"));

        var launched = second.Coordinator.Launch();
        AssertStarted(launched);
        Assert.Contains("backed up", launched.Message, StringComparison.Ordinal);
        Assert.Equal(empty.Digest(), LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).Digest());
        Assert.Equal(secondSettings, MedalFixture.Settings(second.Database));
        string backup = Directory.GetFiles(_fixture.PathFor("second", "backups"), "*.db").Except(earlierBackups).Single();
        Assert.Equal(old.Digest(), LibraryDatabase.ReadSnapshot(backup, MedalFixture.AccountId).Digest());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyGenerationStillRequiresReviewToReplaceUnsharedWork(bool replaceLocal)
    {
        var first = Computer(FirstPc, "first");
        AssertStarted(first.Coordinator.Launch());
        MedalFixture.DeleteContent(first.Database, "clip-1");
        first.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, first.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        var second = Computer(SecondPc, "second", replaceLocal: replaceLocal);
        Record(second, "unshared");
        string before = LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).Digest();

        var launched = second.Coordinator.Launch();

        if (replaceLocal)
        {
            AssertStarted(launched);
            Assert.Contains("backed up", launched.Message, StringComparison.Ordinal);
            Assert.Equal(_store.Generations.Read(1).Snapshot.Digest(), LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).Digest());
        }
        else
        {
            Assert.Equal(LaunchStatus.Blocked, launched.Status);
            Assert.Contains("never shared", launched.Message, StringComparison.Ordinal);
            Assert.Equal(before, LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).Digest());
            Assert.Empty(second.Mount.Starts);
        }
    }

    [Fact]
    public void Finish_WaitsForUploadsAndNeverPublishesOnUnknownStatus()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        pc.Mount.Steady = DrainVerdict.Unknown("rclone did not answer vfs/stats.");

        var pending = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Pending, pending.Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);

        pc.Mount.Steady = new DrainVerdict(DrainState.Failed, ["1 file(s) failed to upload."], 1, 10);
        Assert.Equal(FinishStatus.Blocked, pc.Coordinator.Finish(session).Status);

        pc.Mount.Verdicts.Enqueue(new DrainVerdict(DrainState.Pending, ["1 upload(s) remaining"], 1, 10));
        pc.Mount.Steady = new DrainVerdict(DrainState.Drained, [], 0, 0);
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
    }

    [Fact]
    public void Finish_DoesNothingWhileMedalRuns()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        var outcome = pc.Coordinator.Finish(_store.ReadJournal().SessionId!);
        Assert.Equal(FinishStatus.Pending, outcome.Status);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
    }

    [Fact]
    public void Finish_EndsAnUnchangedSessionWithoutANewGeneration()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        pc.Medal.Running = false;

        var outcome = pc.Coordinator.Finish(_store.ReadJournal().SessionId!);

        Assert.Equal(FinishStatus.Unchanged, outcome.Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal([0L], _store.Generations.Numbered());
    }

    [Fact]
    public void Finish_BlocksWhenMedalDroppedClipsThatStillExist()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        string session = _store.ReadJournal().SessionId!;
        // Medal's missing-media handling during an outage clears the path.
        using (var connection = MedalFixture.Open(pc.Database))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET video_path = NULL WHERE local_content_id = 'clip-1'");
        }
        pc.Medal.Running = false;

        var blocked = pc.Coordinator.Finish(session);
        Assert.Equal(FinishStatus.Blocked, blocked.Status);
        Assert.Contains("audit", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);

        // A deliberate removal of the file too is a normal deletion.
        _assets.OnServerFiles.Remove(@"M:\Clips\Clips\one.mp4");
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopReview_AcceptsOnlyConfirmedRemovalsAndAllowsRelaunch(bool accept)
    {
        LibraryChangeReview? shown = null;
        var pc = Computer(FirstPc, "first", reviewLibraryChanges: review => { shown = review; return accept; });
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        pc.Medal.Running = false;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id = 'clip-1'");

        // Exercise the desktop restart path: it resolves the old session and then starts Medal.
        var outcome = pc.Coordinator.Launch();

        Assert.NotNull(shown);
        Assert.Equal(session, shown.SessionId);
        Assert.Single(shown.Changes);
        Assert.Equal(MediaChangeKind.RemovedWithoutRecord, shown.Changes[0].Kind);
        Assert.Equal(accept ? LaunchStatus.Started : LaunchStatus.Blocked, outcome.Status);
        Assert.Equal(accept ? 1 : 0, _store.ReadJournal().CommittedGeneration);
        Assert.Equal(accept ? 2 : 1, pc.Medal.Launches);
        Assert.Contains(@"M:\Clips\Clips\one.mp4", _assets.OnServerFiles);
        if (!accept) Assert.Empty(pc.Mount.Forgotten);
    }

    [Theory]
    [InlineData("library")]
    [InlineData("medal")]
    [InlineData("ownership")]
    public void DesktopReview_RefusesChangesMadeWhileTheDialogWasOpen(string changed)
    {
        Pc? pc = null;
        pc = Computer(FirstPc, "first", reviewLibraryChanges: review =>
        {
            if (changed == "library") MedalFixture.AddContent(pc!.Database, "unreviewed");
            if (changed == "medal") pc!.Medal.Running = true;
            if (changed == "ownership")
                _store.Update(r => Ownership.Abandon(r, SecondPc, review.SessionId, "test recovery", _clock.Now), TimeSpan.FromSeconds(1));
            return true;
        });
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        pc.Medal.Running = false;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id = 'clip-1'");

        var outcome = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, outcome.Status);
        Assert.Contains("changed during review", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void DesktopReview_DoesNotBypassRemainingMissingMedia()
    {
        var pc = Computer(FirstPc, "first", reviewLibraryChanges: _ => true);
        AssertStarted(pc.Coordinator.Launch());
        pc.Medal.Running = false;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id = 'clip-1'");
        MedalFixture.AddContent(pc.Database, "new-missing", video: @"M:\Clips\Clips\missing.mp4");

        var outcome = pc.Coordinator.Finish(_store.ReadJournal().SessionId!);

        Assert.Equal(FinishStatus.Blocked, outcome.Status);
        Assert.Contains("not on the server", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
    }

    [Fact]
    public void DesktopReview_IncludesCloudOnlyRemovalsAndPreservesBothSnapshots()
    {
        LibraryChangeReview? shown = null;
        var pc = Computer(FirstPc, "first", reviewLibraryChanges: review => { shown = review; return true; });
        AssertStarted(pc.Coordinator.Launch());
        MedalFixture.AddContent(pc.Database, "cloud-only", metadata: "{\"title\":\"\",\"remoteContent\":{\"contentTitle\":\"Cloud clip\"}}");
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);
        AssertStarted(pc.Coordinator.Launch());
        pc.Medal.Running = false;
        using (var connection = MedalFixture.Open(pc.Database))
            MedalFixture.Execute(connection, "DELETE FROM contents");

        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(_store.ReadJournal().SessionId!).Status);

        Assert.NotNull(shown);
        Assert.Equal(2, shown.Changes.Count);
        string page = Assert.Single(shown.Pages());
        Assert.Contains("one.mp4", page, StringComparison.Ordinal);
        Assert.Contains("Cloud clip", page, StringComparison.Ordinal);
        Assert.Equal(2, shown.Before.RowCount("contents"));
        Assert.Equal(0, shown.After.RowCount("contents"));
    }

    [Fact]
    public void Finish_BlocksWhileAFileIsNotOnTheServer()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        MedalFixture.AddContent(pc.Database, "clip-2", video: @"M:\Clips\Clips\two.mp4");
        pc.Medal.Running = false;

        var outcome = pc.Coordinator.Finish(_store.ReadJournal().SessionId!);

        Assert.Equal(FinishStatus.Blocked, outcome.Status);
        Assert.Contains("two.mp4", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
    }

    [Fact]
    public void Finish_ReusesItsOwnPublishedGenerationAfterACrash()
    {
        var pc = Computer(FirstPc, "first");
        pc.Coordinator.Launch();
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        // Simulate a crash after the generation was written but before commit.
        pc.Mount.Stop();
        _store.Update(r => Ownership.BeginDrain(r, FirstPc, session, _clock.Now), TimeSpan.FromSeconds(1));
        _store.Update(r => Ownership.BeginPublish(r, FirstPc, session, _clock.Now), TimeSpan.FromSeconds(1));
        var snapshot = LibraryDatabase.ReadSnapshot(pc.Database, MedalFixture.AccountId);
        _store.Generations.Publish(1, 0, session, FirstPc, snapshot, new AssetManifest(1, [], []), Versions, _clock.Now);

        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(1, _store.ReadJournal().CommittedGeneration);
    }

    [Fact]
    public void Finish_KeepsTheSessionWhenMedalIsReopenedDuringTheDrain()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        pc.Mount.OnDrain = () => pc.Medal.Running = true;

        var outcome = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Pending, outcome.Status);
        Assert.Equal(OwnershipState.Active, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.True(pc.Mount.IsRunning);
        Assert.Empty(pc.Mount.Forgotten);

        pc.Mount.OnDrain = null;
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
    }

    [Fact]
    public void Finish_NeverPublishesWhileMedalRunsAfterTheDriveStopped()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        pc.Mount.OnStop = () => pc.Medal.Running = true;

        var outcome = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Blocked, outcome.Status);
        Assert.Contains("medal-sync finish", outcome.Message, StringComparison.Ordinal);
        var journal = _store.ReadJournal();
        Assert.Equal(OwnershipState.Draining, journal.State);
        Assert.Equal(FirstPc, journal.Owner);
        Assert.Equal(0, journal.CommittedGeneration);

        pc.Mount.OnStop = null;
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.False(pc.Mount.ForRecording[^1]);
    }

    [Fact]
    public void Finish_PublishesWhenTheCacheCannotBeRemovedAndLaterRunsRetryIt()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        Assert.Equal(1, pc.Mount.FinishedCleanups);
        string session = _store.ReadJournal().SessionId!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        pc.Mount.ForgetFailure = new IOException("Synthetic: a backup client holds a cached clip.");
        pc.Mount.FinishedLeft.Add("Finished session x: still held.");

        var finished = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Published, finished.Status);
        Assert.Equal(new[] { finished.Generation }, pc.Mount.ForgottenGenerations);
        Assert.Contains("could not be removed yet", finished.Message, StringComparison.Ordinal);
        Assert.Contains("does not block setup", finished.Message, StringComparison.Ordinal);
        Assert.Contains("still held", finished.Message, StringComparison.Ordinal);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(2, pc.Mount.FinishedCleanups);

        pc.Mount.ForgetFailure = null;
        AssertStarted(pc.Coordinator.Launch());
        Assert.Equal(3, pc.Mount.FinishedCleanups);
    }

    [Fact]
    public void Finish_SkipsAGenerationLeftByAnAbandonedSession()
    {
        var first = Computer(FirstPc, "first");
        AssertStarted(first.Coordinator.Launch());
        string lost = _store.ReadJournal().SessionId!;
        Record(first, "clip-2");
        // The first PC wrote generation 1 and died before committing it; the owner gave its session up.
        _store.Generations.Publish(1, 0, lost, FirstPc, LibraryDatabase.ReadSnapshot(first.Database, MedalFixture.AccountId),
            new AssetManifest(1, [], []), Versions, _clock.Now);
        _store.Update(r => Ownership.Abandon(r, SecondPc, lost, "first PC lost", _clock.Now), TimeSpan.FromSeconds(1));

        var second = Computer(SecondPc, "second");
        AssertStarted(second.Coordinator.Launch());
        Assert.Equal(1, LibraryDatabase.ReadSnapshot(second.Database, MedalFixture.AccountId).RowCount("contents"));
        Record(second, "clip-3");
        second.Medal.Running = false;
        var outcome = second.Coordinator.Finish(_store.ReadJournal().SessionId!);

        Assert.Equal(FinishStatus.Published, outcome.Status);
        Assert.Equal(2, outcome.Generation);
        Assert.Equal(0, _store.Generations.ReadManifest(2).ParentGeneration);
        Assert.Equal(2, _store.ReadJournal().CommittedGeneration);
    }

    [Fact]
    public void Finish_WaitsWhenTheServerCannotBeReached()
    {
        var pc = Computer(FirstPc, "first");
        AssertStarted(pc.Coordinator.Launch());
        string session = _store.ReadJournal().SessionId!;
        // During an outage every file looks deleted; that must not pass as a clean session.
        using (var connection = MedalFixture.Open(pc.Database))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET video_path = NULL WHERE local_content_id = 'clip-1'");
        }
        _assets.OnServerFiles.Clear();
        _assets.ServerReachable = false;
        pc.Medal.Running = false;

        var outcome = pc.Coordinator.Finish(session);

        Assert.Equal(FinishStatus.Pending, outcome.Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
    }

    [Fact]
    public void FailedReplicaRetainsOwnershipCacheAndGenerationThenRetries()
    {
        var replication = new FakeReplication { OnPublish = () => throw new IOException("provider quota") };
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Finish(session).Status);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
        Assert.True(pc.Mount.IsRunning);
        replication.OnPublish = null;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(2, replication.Published);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void UnchangedLibraryStillRequiresReplicaVerification()
    {
        var replication = new FakeReplication();
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        Assert.Equal(FinishStatus.Unchanged, pc.Coordinator.Finish(session).Status);
        Assert.Equal(1, replication.Published);
    }

    [Fact]
    public void ReopeningMedalDuringReplicationPreventsPublication()
    {
        var replication = new FakeReplication();
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        pc.Medal.Running = false;
        replication.OnPublish = () => pc.Medal.Running = true;
        Assert.Equal(FinishStatus.Pending, pc.Coordinator.Finish(session).Status);
        Assert.Equal(0, _store.ReadJournal().CommittedGeneration);
        Assert.Empty(pc.Mount.Forgotten);
    }

    [Fact]
    public void OfflinePrimaryAtFinishWaitsCalmlyRetainsOwnershipAndFinishesWhenBack()
    {
        List<SessionProgress> reports = [];
        var replication = new FakeReplication { OnPublish = () => throw new StorageOfflineException("synthetic outage") };
        var pc = Computer(FirstPc, "first", replication: replication, progress: reports.Add);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        var waiting = pc.Coordinator.Finish(session);
        Assert.Equal(FinishStatus.Pending, waiting.Status);
        Assert.True(waiting.StorageOffline);
        Assert.StartsWith("Waiting for storage", waiting.Message);
        Assert.Equal(SessionPhase.WaitingForStorage, reports.Last(r => r.IsOutcome).Phase);
        Assert.Equal(OwnershipState.Draining, _store.ReadJournal().State);
        Assert.Empty(pc.Mount.Forgotten);
        replication.OnPublish = null;
        Assert.Equal(FinishStatus.Published, pc.Coordinator.Finish(session).Status);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void DeferredOptionalReplicaStillReleasesTheLibraryAndIsNamed()
    {
        var replication = new FakeReplication { Deferred = ["cloud"] };
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        var finished = pc.Coordinator.Finish(session);
        Assert.Equal(FinishStatus.Published, finished.Status);
        Assert.Contains("'cloud'", finished.Message);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void ActionNeededFinishSucceedsOnTheFirstRetryAfterTheCauseIsFixed()
    {
        bool broken = true;
        var replication = new FakeReplication { OnPublish = () =>
        {
            if (broken) throw new StorageActionRequiredException("Primary storage onedrive:MedalSync did not accept rclone's sign-in.");
        } };
        var pc = Computer(FirstPc, "first", replication: replication);
        string session = pc.Coordinator.Launch().Session!;
        Record(pc, "clip-2");
        pc.Medal.Running = false;
        _clock.OnSleep = () => broken = false;

        var outcome = pc.Coordinator.Supervise(session, _ => { }, CancellationToken.None);

        Assert.Equal(FinishStatus.Published, outcome.Status);
        Assert.Equal(2, replication.Published);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }
}

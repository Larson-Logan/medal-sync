using System.Runtime.Versioning;
using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary><c>primary prune</c> and <c>primary purge-trash</c> against a synthetic folder primary.</summary>
[SupportedOSPlatform("windows")]
public sealed class PrimaryPruneTests : IDisposable
{
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");
    private readonly MedalFixture _fixture = new();
    private readonly DeviceIdentity _first = new(new string('a', 32), "First PC");
    private readonly MedalSyncConfig _config;
    private readonly LocalState _state;
    private readonly CoordinationStore _store;
    private readonly string _db;
    private bool _idle = true;
    private int? _failMoveAfter;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public PrimaryPruneTests()
    {
        string credentials = _fixture.PathFor("private", "rclone.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(credentials)!);
        File.WriteAllText(credentials, "[synth]\ntype = local\n");
        _config = new(1, new string('c', 32), MedalFixture.AccountId, _fixture.PathFor("coordination"),
            _fixture.PathFor("server"), @"M:\Clips", _fixture.PathFor("cache"), @"C:\Tools\rclone.exe",
            new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, [])
        {
            Replication = new(null, credentials, []) { FullVerifyEveryHandoffs = 100, FullVerifyEveryDays = 30 },
        };
        _state = new(_fixture.PathFor("state"));
        StorageIdentity.Initialize(_config.ServerCapture.Display, _config.LibraryId);
        _db = _fixture.CreateLibrary("first-pc");
        foreach (string name in new[] { "one", "two" })
        {
            Primary(name + ".mp4", "bytes-" + name, _now.AddDays(-60));
            MedalFixture.AddContent(_db, name, video: $@"M:\Clips\{name}.mp4");
        }
        _store = CoordinationStore.Create(_config.LibraryCoordinationDirectory, _config.LibraryId,
            LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId), new(1, [], []), _first, SessionCoordinator.NewId(), Versions,
            _now.AddDays(-100));
    }

    public void Dispose() => _fixture.Dispose();

    private string Root => _config.ServerCapture.Display;

    private void Primary(string relative, string content, DateTimeOffset modified)
    {
        string path = Path.Combine(Root, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
    }

    private void RequireIdle()
    {
        if (!_idle) throw new OperationCanceledException("Synthetic launch on this PC.");
    }

    private ReplicaPrune Prune() => PrimaryCommands.Prune(_config, _state, _store, RequireIdle, new Failing(new PrimaryFolderRemote(RequireIdle), this), () => _now);

    /// <summary>The real folder remote, with an outage injected after a number of moves.</summary>
    private sealed class Failing(IReplicaRemote inner, PrimaryPruneTests test) : IReplicaRemote
    {
        private int _moves;
        public IReadOnlyList<RemoteFile> List(string destination) => inner.List(destination);
        public RemoteCheckOutcome Check(string source, string destination, IReadOnlyList<string> files, Action? guard = null) => inner.Check(source, destination, files, guard);
        public void Move(string destination, IReadOnlyList<string> files, string folder)
        {
            foreach (string file in files)
            {
                if (test._failMoveAfter is { } limit && _moves >= limit) throw new StorageOfflineException("Synthetic outage during move.");
                inner.Move(destination, [file], folder);
                _moves++;
            }
        }
        public void Mark(string destination, string folder, string name) => inner.Mark(destination, folder, name);
        public void Purge(string destination, string folder) => inner.Purge(destination, folder);
    }

    private string[] Tree() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A clip deleted in Medal, recent and unrelated files, Medal's working folder and Medal Sync's own files.</summary>
    private void Clutter()
    {
        Primary("deleted.mp4", "deleted in Medal", _now.AddDays(-60));
        Primary("Thumbnails/deleted.jpg", "thumb", _now.AddDays(-60));
        Primary("Clips/one.mp4", "same name, other folder, unreferenced", _now.AddDays(-60));
        Primary("recent.mp4", "changed this week", _now.AddDays(-2));
        Primary("notes.txt", "not media", _now.AddDays(-60));
        Primary("Temp/RecordingBuffer/segment.mp4", "replay buffer", _now.AddDays(-60));
        Primary(".medal-sync-preserved/old.mp4", "earlier Medal Sync data", _now.AddDays(-60));
    }

    private void Commit(DateTimeOffset at)
    {
        string session = SessionCoordinator.NewId();
        void Change(Func<OwnershipRecord, OwnershipRecord> t) => _store.Update(t, TimeSpan.FromSeconds(1));
        Change(r => Ownership.Acquire(r, _first, session, at));
        Change(r => Ownership.MarkActive(r, _first, session, at));
        Change(r => Ownership.BeginDrain(r, _first, session, at));
        Change(r => Ownership.BeginPublish(r, _first, session, at));
        long committed = _store.ReadJournal().CommittedGeneration;
        long next = _store.Generations.Next(committed);
        _store.Generations.Publish(next, committed, session, _first, LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId), new(1, [], []), Versions, at);
        Change(r => Ownership.CompletePublish(r, _first, session, next, at));
    }

    [Fact]
    public void PlansOnlyOldUnreferencedClipMediaAndMovesItWithoutDeleting()
    {
        Clutter();
        var plan = Prune().Plan(ReplicaPrune.PrimaryTarget);
        Assert.Equal(["Clips/one.mp4", "Thumbnails/deleted.jpg", "deleted.mp4"], plan.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(Root, plan.Destination);
        Assert.True(plan.Required);
        string text = ReplicaPrune.Render(plan);
        Assert.Contains("Primary storage", text, StringComparison.Ordinal);
        Assert.Contains("Every PC must stay Ready", text, StringComparison.Ordinal);

        var result = Prune().Apply(plan);

        Assert.Equal(3, result.Moved);
        foreach (string kept in new[] { "one.mp4", "two.mp4", "recent.mp4", "notes.txt", "Temp/RecordingBuffer/segment.mp4",
            ".medal-sync-preserved/old.mp4", MedalSyncConfig.StorageMarker })
            Assert.True(File.Exists(Path.Combine(Root, kept)), kept);
        foreach (var file in plan.Files)
        {
            Assert.False(File.Exists(Path.Combine(Root, file.Path)), file.Path);
            Assert.True(File.Exists(Path.Combine(Root, ".medal-sync-trash", plan.Id, file.Path)), file.Path);
        }
    }

    [Fact]
    public void AClipReferencedOnlyByAnOlderKeptGenerationIsNeverPlanned()
    {
        Clutter();
        // Generation 1 referenced the clip; it was then deleted in Medal and generation 2 committed without it.
        MedalFixture.AddContent(_db, "deleted", video: @"M:\Clips\deleted.mp4");
        Commit(_now.AddDays(-90));
        MedalFixture.DeleteContent(_db, "deleted");
        Commit(_now.AddDays(-80));
        Assert.Equal(2, _store.ReadJournal().CommittedGeneration);
        Assert.DoesNotContain(Prune().Plan(ReplicaPrune.PrimaryTarget).Files, f => f.Path == "deleted.mp4");
    }

    [Fact]
    public void AFileNamedOnlyByAnUnrecognizedMetadataFieldIsNeverPlanned()
    {
        Clutter();
        MedalFixture.AddContent(_db, "plugin", video: @"M:\Clips\two.mp4",
            metadata: "{\"title\":\"Synthetic clip\",\"plugin\":{\"cache\":\"M:\\\\Clips\\\\deleted.mp4\"}}");
        Commit(_now.AddDays(-80));

        var plan = Prune().Plan(ReplicaPrune.PrimaryTarget);

        Assert.DoesNotContain(plan.Files, f => f.Path == "deleted.mp4");
        Assert.Contains(plan.Files, f => f.Path == "Thumbnails/deleted.jpg");
    }

    [Fact]
    public void AnOldUncommittedAttemptStillProtectsItsFiles()
    {
        Clutter();
        MedalFixture.AddContent(_db, "deleted", video: @"M:\Clips\deleted.mp4");
        long head = _store.ReadJournal().CommittedGeneration;
        _store.Generations.Publish(head + 1, head, SessionCoordinator.NewId(), _first, LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId),
            new(1, [], []), Versions, _now.AddDays(-365));
        Assert.DoesNotContain(Prune().Plan(ReplicaPrune.PrimaryTarget).Files, f => f.Path == "deleted.mp4");
    }

    [Fact]
    public void AnUnreadableGenerationRefusesTheWholeReview()
    {
        Clutter();
        Commit(_now.AddDays(-90));
        Commit(_now.AddDays(-80));
        File.WriteAllText(Path.Combine(_store.Generations.Directory, GenerationStore.Name(1), "library.json"), "{ damaged");
        var refusal = Assert.Throws<UnsafeOperationException>(() => Prune().Plan(ReplicaPrune.PrimaryTarget));
        Assert.Contains("Generation 1 cannot be read", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecentlyChangedFileIsKeptUntilTheGracePeriodEnds()
    {
        Clutter();
        Assert.DoesNotContain(Prune().Plan(ReplicaPrune.PrimaryTarget).Files, f => f.Path == "recent.mp4");
        Assert.Contains(Prune().Plan(ReplicaPrune.PrimaryTarget, graceDays: 1).Files, f => f.Path == "recent.mp4");
    }

    [Fact]
    public void AFileThatChangedSinceTheReviewRefusesAndMovesNothing()
    {
        Clutter();
        var plan = Prune().Plan(ReplicaPrune.PrimaryTarget);
        Primary("deleted.mp4", "replaced by another PC", _now.AddDays(-30));
        var before = Tree();
        Assert.Throws<UnsafeOperationException>(() => Prune().Apply(plan));
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void ANewCommittedGenerationOrABusyLibraryRefusesTheReview()
    {
        Clutter();
        var plan = Prune().Plan(ReplicaPrune.PrimaryTarget);
        Commit(_now);
        var before = Tree();
        Assert.Throws<UnsafeOperationException>(() => Prune().Apply(plan));
        var fresh = Prune().Plan(ReplicaPrune.PrimaryTarget);
        _store.Update(r => Ownership.Acquire(r, _first, SessionCoordinator.NewId(), DateTimeOffset.UtcNow), TimeSpan.FromSeconds(1));
        var busy = Assert.Throws<UnsafeOperationException>(() => Prune().Apply(fresh));
        Assert.Contains("every PC to be Ready", busy.Message, StringComparison.Ordinal);
        Assert.Throws<UnsafeOperationException>(() => Prune().Plan(ReplicaPrune.PrimaryTarget));
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void AnotherPcHoldingTheWriterLockRefusesBeforeMoving()
    {
        Clutter();
        var plan = Prune().Plan(ReplicaPrune.PrimaryTarget);
        var otherPc = CoordinationStore.Open(_config.LibraryCoordinationDirectory, _config.LibraryId, _config.AccountId);
        using var held = otherPc.AcquireWriterLock(TimeSpan.FromSeconds(1));
        var before = Tree();
        Assert.Throws<StorageBusyException>(() => ReplicaCommands.ApplyPrune(Prune(), _store, plan, TimeSpan.Zero));
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void AnInterruptedApplyFinishesWhenTheSameReviewIsAppliedAgain()
    {
        Clutter();
        var plan = Prune().Plan(ReplicaPrune.PrimaryTarget);
        _failMoveAfter = 1;
        Assert.Throws<StorageOfflineException>(() => Prune().Apply(plan));
        _failMoveAfter = null;
        var result = Prune().Apply(plan);
        Assert.Equal((2, 1), (result.Moved, result.AlreadyMoved));
        Assert.All(plan.Files, f => Assert.True(File.Exists(Path.Combine(Root, ".medal-sync-trash", plan.Id, f.Path))));
    }

    [Fact]
    public void ALaunchOnThisPcStopsBeforeMoving()
    {
        Clutter();
        var plan = Prune().Plan(ReplicaPrune.PrimaryTarget);
        _idle = false;
        var before = Tree();
        Assert.Throws<OperationCanceledException>(() => Prune().Apply(plan));
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void PrimaryAndCloudCopyReviewsCannotBeSwapped()
    {
        Clutter();
        var plan = Prune().Plan(ReplicaPrune.PrimaryTarget);
        var replica = new ReplicaPrune(_config, _state, _store, RequireIdle, new PrimaryFolderRemote(RequireIdle), () => _now);
        var refusal = Assert.Throws<UnsafeOperationException>(() => replica.Apply(plan));
        Assert.Contains("primary prune", refusal.Message, StringComparison.Ordinal);
        Assert.Throws<UnsafeOperationException>(() => Prune().Plan("cloud"));
    }

    [Fact]
    public void ApplyingMakesTheNextHandoffReadEveryReferencedFileBack()
    {
        Clutter();
        string digest = _store.Generations.Read(0).Snapshot.Digest();
        bool full = false;
        void Handoff() => new ReplicationVerification(_config, _state, Root, _config.PrimaryCapture, digest,
            ["one.mp4", "two.mp4"], _now).Run((_, _) => { }, _ => { }, (_, _, f) => full = f, RequireIdle);
        Handoff();
        Handoff();
        Assert.False(full);
        Prune().Apply(Prune().Plan(ReplicaPrune.PrimaryTarget));
        Handoff();
        Assert.True(full);
    }

    [Fact]
    public void PurgeDeletesOnlyWholeBatchesPastTheAgeLimit()
    {
        Clutter();
        var plan = Prune().Plan(ReplicaPrune.PrimaryTarget);
        Prune().Apply(plan);
        var batch = Assert.Single(Prune().Trash(ReplicaPrune.PrimaryTarget));
        Assert.Equal(3, batch.Files);
        Assert.Empty(Prune().Purge(ReplicaPrune.PrimaryTarget, ReplicaPrune.DefaultPurgeDays, apply: true));
        Assert.True(Directory.Exists(Path.Combine(Root, ".medal-sync-trash", plan.Id)));
        _now = _now.AddDays(8);
        Assert.Single(Prune().Purge(ReplicaPrune.PrimaryTarget, ReplicaPrune.DefaultPurgeDays, apply: false));
        Assert.True(Directory.Exists(Path.Combine(Root, ".medal-sync-trash", plan.Id)));
        Assert.Single(Prune().Purge(ReplicaPrune.PrimaryTarget, ReplicaPrune.DefaultPurgeDays, apply: true));
        Assert.False(Directory.Exists(Path.Combine(Root, ".medal-sync-trash", plan.Id)));
        Assert.True(File.Exists(Path.Combine(Root, "one.mp4")));
    }

    [Fact]
    public void TheFolderRemoteRefusesEscapingPaths()
    {
        var remote = new PrimaryFolderRemote(() => { });
        Assert.Throws<ArgumentException>(() => remote.Move(Root, ["../outside.mp4"], ".medal-sync-trash/x"));
        Assert.Throws<ArgumentException>(() => remote.Purge(Root, ".."));
        Assert.Empty(remote.List(Path.Combine(Root, "missing")));
    }

    [Fact]
    public void CommandsNeedAReviewFileOrAnExactApply()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandRunner.Run(["primary", "prune"], output, error));
        Assert.Equal(2, CommandRunner.Run(["primary", "prune", "--in", "x.json", "--apply", "--grace-days", "3"], output, error));
        Assert.Contains("primary prune --out FILE", error.ToString(), StringComparison.Ordinal);
    }
}

using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>A synthetic destination: a local folder per remote path, with optional failures injected.</summary>
internal sealed class FolderRemote(string root) : IReplicaRemote
{
    public Func<RemoteCheckOutcome?>? OnCheck { get; set; }
    public int? FailMoveAfter { get; set; }
    public int Moves { get; private set; }

    public string Folder(string destination) => Path.Combine([root, .. destination.Replace(":", "/", StringComparison.Ordinal).Split('/')]);

    public IReadOnlyList<RemoteFile> List(string destination)
    {
        string folder = Folder(destination);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => new RemoteFile(Path.GetRelativePath(folder, f).Replace('\\', '/'), new FileInfo(f).Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero)))
            .ToArray();
    }

    public RemoteCheckOutcome Check(string source, string destination, IReadOnlyList<string> files)
    {
        if (OnCheck?.Invoke() is { } injected) return injected;
        var results = new Dictionary<string, RemoteCheck>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string there = Path.Combine(Folder(destination), file.Replace('/', '\\'));
            results[file] = !File.Exists(there) ? RemoteCheck.Missing
                : File.ReadAllBytes(there).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(source, file.Replace('/', '\\')))) ? RemoteCheck.Match
                : RemoteCheck.Differs;
        }
        return new(results, null);
    }

    public void Move(string destination, IReadOnlyList<string> files, string folder)
    {
        foreach (string file in files)
        {
            if (FailMoveAfter is { } limit && Moves >= limit) throw new RcloneFailedException("Synthetic outage during move.", 5);
            string from = Path.Combine(Folder(destination), file.Replace('/', '\\'));
            string to = Path.Combine(Folder(destination), folder.Replace('/', '\\'), file.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            Moves++;
        }
    }

    public void Purge(string destination, string folder) =>
        Directory.Delete(Path.Combine(Folder(destination), folder.Replace('/', '\\')), recursive: true);
}

/// <summary><c>replica verify</c> and <c>replica prune</c> against a synthetic destination while the library is Ready.</summary>
public sealed class ReplicaMaintenanceTests : IDisposable
{
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");
    private readonly MedalFixture _fixture = new();
    private readonly DeviceIdentity _first = new(new string('a', 32), "First PC");
    private readonly MedalSyncConfig _config;
    private readonly LocalState _state;
    private readonly CoordinationStore _store;
    private readonly FolderRemote _remote;
    private readonly string _db;
    private bool _idle = true;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public ReplicaMaintenanceTests()
    {
        string credentials = _fixture.PathFor("private", "rclone.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(credentials)!);
        File.WriteAllText(credentials, "[synth]\ntype = local\n");
        _config = new(1, new string('c', 32), MedalFixture.AccountId, _fixture.PathFor("coordination"),
            _fixture.PathFor("server"), @"M:\Clips", _fixture.PathFor("cache"), @"C:\Tools\rclone.exe",
            new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, [])
        {
            Replication = new(null, credentials, [new ReplicaTarget("cloud", "synth:Medal"), new ReplicaTarget("vault", "synth:Vault") { Required = true }]),
        };
        _state = new(_fixture.PathFor("state"));
        _remote = new(_fixture.PathFor("remote"));
        StorageIdentity.Initialize(_config.ServerCapture.Display, _config.LibraryId);
        foreach (string name in new[] { "one.mp4", "two.mp4" })
        {
            File.WriteAllText(_config.ServerCapture.Append(name).Display, "bytes-" + name);
            Directory.CreateDirectory(Destination);
            File.Copy(_config.ServerCapture.Append(name).Display, Path.Combine(Destination, name));
        }
        _db = _fixture.CreateLibrary("first-pc");
        MedalFixture.AddContent(_db, "one", video: @"M:\Clips\one.mp4");
        MedalFixture.AddContent(_db, "two", video: @"M:\Clips\two.mp4");
        _store = CoordinationStore.Create(_config.LibraryCoordinationDirectory, _config.LibraryId,
            LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId), new(1, [], []), _first, SessionCoordinator.NewId(), Versions,
            DateTimeOffset.UtcNow.AddDays(-100));
    }

    public void Dispose() => _fixture.Dispose();

    private string Destination => _remote.Folder("synth:Medal/" + _config.LibraryId);

    private void RequireIdle()
    {
        if (!_idle) throw new OperationCanceledException("Synthetic launch on this PC.");
    }

    private ReplicaAudit Audit() => new(_config, _state, _store, RequireIdle, _remote, clock: () => _now);
    private ReplicaPrune Prune() => new(_config, _state, _store, RequireIdle, _remote, () => _now);

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

    private string Remote(string relative, string content, DateTimeOffset modified)
    {
        string path = Path.Combine(Destination, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private string[] Tree() => Directory.EnumerateFiles(_fixture.PathFor("remote"), "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(_fixture.PathFor("remote"), p)).Order(StringComparer.Ordinal).ToArray();

    // ---------- replica verify ----------

    [Fact]
    public void Verify_PassesAndRecordsCountsWithoutChangingTheDestination()
    {
        var before = Tree();
        var plan = Audit().Plan("cloud");
        Assert.Equal(2, plan.Files);
        var attempt = Assert.Single(Audit().Apply(plan));
        Assert.Equal(ReplicaAudit.Passed, attempt.Result);
        Assert.Equal(2, attempt.Matched);
        Assert.Equal(before, Tree());
        var history = Assert.Single(ReplicaAudit.Read(_state, _config.LibraryId).Targets);
        Assert.Equal(attempt, history.LastComplete);
        Assert.Equal(attempt, history.LastPassed);
        Assert.Empty(StorageReplication.Pending(_state, _config.LibraryId));
        Assert.DoesNotContain("one.mp4", File.ReadAllText(ReplicaAudit.HistoryPath(_state)), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_MissingOrDifferentFilesFailAndQueueAnOptionalRetry()
    {
        File.Delete(Path.Combine(Destination, "two.mp4"));
        File.WriteAllText(Path.Combine(Destination, "one.mp4"), "damaged");
        var attempt = Assert.Single(Audit().Apply(Audit().Plan("cloud")));
        Assert.Equal(ReplicaAudit.Failed, attempt.Result);
        Assert.Equal((1, 1, 0), (attempt.Missing, attempt.Mismatched, attempt.Matched));
        Assert.Equal("cloud", Assert.Single(StorageReplication.Pending(_state, _config.LibraryId)).Name);
        // The check never repairs: the damaged file is still exactly as found.
        Assert.Equal("damaged", File.ReadAllText(Path.Combine(Destination, "one.mp4")));
        Assert.False(File.Exists(Path.Combine(Destination, "two.mp4")));
    }

    [Fact]
    public void Verify_AnOutageIsIncompleteAndKeepsTheLastCompleteResult()
    {
        var passed = Assert.Single(Audit().Apply(Audit().Plan("cloud")));
        _now = _now.AddHours(1);
        _remote.OnCheck = () => new(new Dictionary<string, RemoteCheck> { ["one.mp4"] = RemoteCheck.Match },
            new RcloneFailedException("Synthetic network outage.", 1, hints: RcloneHints.Network, command: "check"));
        var attempt = Assert.Single(Audit().Apply(Audit().Plan("cloud")));
        Assert.Equal(ReplicaAudit.Incomplete, attempt.Result);
        Assert.Equal("offline", attempt.Reason);
        Assert.Equal((1, 1), (attempt.Matched, attempt.Unknown));
        var history = Assert.Single(ReplicaAudit.Read(_state, _config.LibraryId).Targets);
        Assert.Equal(passed, history.LastComplete);
        Assert.Equal(passed, history.LastPassed);
        Assert.Equal(2, history.Attempts.Count);
        Assert.Contains("incomplete (offline)", ReplicaAudit.Render(history.Attempts[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_ALaunchOnThisPcCancelsAndIsRecordedAsIncomplete()
    {
        var plan = Audit().Plan("cloud");
        _remote.OnCheck = () => { _idle = false; return null; };
        Assert.Throws<OperationCanceledException>(() => Audit().Apply(plan));
        var history = Assert.Single(ReplicaAudit.Read(_state, _config.LibraryId).Targets);
        Assert.Null(history.LastComplete);
        Assert.Equal("canceled", Assert.Single(history.Attempts).Reason);
    }

    [Fact]
    public void Verify_AnotherPcStartingMakesTheResultDescribeAnOldGenerationAndIsDiscarded()
    {
        var plan = Audit().Plan("cloud");
        _remote.OnCheck = () => { Commit(DateTimeOffset.UtcNow); return null; };
        Assert.Throws<LibraryChangedException>(() => Audit().Apply(plan));
        var history = Assert.Single(ReplicaAudit.Read(_state, _config.LibraryId).Targets);
        Assert.Null(history.LastComplete);
        Assert.Equal("library-changed", Assert.Single(history.Attempts).Reason);
        Assert.Empty(StorageReplication.Pending(_state, _config.LibraryId));
    }

    [Fact]
    public void Verify_RefusesWhileTheLibraryIsInUse()
    {
        string session = SessionCoordinator.NewId();
        _store.Update(r => Ownership.Acquire(r, _first, session, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(1));
        Assert.Throws<UnsafeOperationException>(() => Audit().Plan());
        Assert.False(File.Exists(ReplicaAudit.HistoryPath(_state)));
    }

    [Fact]
    public void Verify_HistoryKeepsTheNewestTwentyAttempts()
    {
        for (int i = 0; i < 22; i++)
        {
            _now = _now.AddMinutes(1);
            Audit().Apply(Audit().Plan("cloud"));
        }
        var history = Assert.Single(ReplicaAudit.Read(_state, _config.LibraryId).Targets);
        Assert.Equal(20, history.Attempts.Count);
        Assert.Equal(_now, history.Attempts[0].FinishedAt);
    }

    // ---------- replica prune ----------

    private void Clutter()
    {
        Remote("old.mp4", "unreferenced", _now.AddDays(-60));
        Remote("Thumbnails/old.jpg", "thumb", _now.AddDays(-60));
        Remote("recent.mp4", "still uploading elsewhere", _now.AddDays(-2));
        Remote("notes.txt", "not ours", _now.AddDays(-60));
        Remote(".medal-sync-history/20250101T000000000Z-abc123/one.mp4", "prior one", _now.AddDays(-300));
    }

    [Fact]
    public void Prune_PlansOnlyOldUnreferencedMediaAndMovesItToTrash()
    {
        Clutter();
        var plan = Prune().Plan("cloud");
        Assert.Equal(["Thumbnails/old.jpg", "old.mp4"], plan.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.All(plan.Files, f => Assert.Equal(ReplicaPrune.Unreferenced, f.Category));
        Assert.Equal(2, plan.LeftAlone); // notes.txt and version history (not opted in)
        Assert.Contains("--history-older-than", ReplicaPrune.Render(plan), StringComparison.Ordinal);

        var result = Prune().Apply(plan);
        Assert.Equal(2, result.Moved);
        Assert.False(File.Exists(Path.Combine(Destination, "old.mp4")));
        Assert.True(File.Exists(Path.Combine(Destination, ".medal-sync-trash", plan.Id, "old.mp4")));
        Assert.True(File.Exists(Path.Combine(Destination, ".medal-sync-trash", plan.Id, "Thumbnails", "old.jpg")));
        foreach (string kept in new[] { "one.mp4", "two.mp4", "recent.mp4", "notes.txt" })
            Assert.True(File.Exists(Path.Combine(Destination, kept)), kept);
        Assert.True(File.Exists(_state.Root + @"\replication\prune\" + plan.Id + ".json"));
    }

    [Fact]
    public void Prune_VersionHistoryIsAnOptInCategory()
    {
        Clutter();
        var plan = Prune().Plan("cloud", historyOlderThanDays: 30);
        var history = Assert.Single(plan.Files, f => f.Category == ReplicaPrune.History);
        Assert.Equal(".medal-sync-history/20250101T000000000Z-abc123/one.mp4", history.Path);
        Assert.DoesNotContain(Prune().Plan("cloud", historyOlderThanDays: 3650).Files, f => f.Category == ReplicaPrune.History);
    }

    [Fact]
    public void Prune_AnUploadBetweenReviewAndApplyRefusesAndMovesNothing()
    {
        Clutter();
        var plan = Prune().Plan("cloud");
        Remote("new-upload.mp4", "arrived after review", _now.AddDays(-30));
        var before = Tree();
        Assert.Throws<UnsafeOperationException>(() => Prune().Apply(plan));
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void Prune_ANewCommittedGenerationRefusesTheOldReview()
    {
        Clutter();
        var plan = Prune().Plan("cloud");
        Commit(DateTimeOffset.UtcNow);
        var before = Tree();
        Assert.Throws<UnsafeOperationException>(() => Prune().Apply(plan));
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void Prune_ARecentUnfinishedSwitchProtectsTheFilesItReferences()
    {
        Clutter();
        MedalFixture.AddContent(_db, "old", video: @"M:\Clips\old.mp4");
        long head = _store.ReadJournal().CommittedGeneration;
        _store.Generations.Publish(head + 1, head, SessionCoordinator.NewId(), _first, LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId),
            new(1, [], []), Versions, _now.AddDays(-1));
        Assert.DoesNotContain(Prune().Plan("cloud").Files, f => f.Path == "old.mp4");
    }

    [Fact]
    public void Prune_AnInterruptedApplyFinishesWhenAppliedAgain()
    {
        Clutter();
        var plan = Prune().Plan("cloud");
        _remote.FailMoveAfter = 1;
        Assert.Throws<RcloneFailedException>(() => Prune().Apply(plan));
        _remote.FailMoveAfter = null;
        var result = Prune().Apply(plan);
        Assert.Equal((1, 1), (result.Moved, result.AlreadyMoved));
        Assert.False(File.Exists(Path.Combine(Destination, "old.mp4")));
        Assert.False(File.Exists(Path.Combine(Destination, "Thumbnails", "old.jpg")));
    }

    [Fact]
    public void Prune_RefusesRequiredCopiesEditedPlansAndBusyLibraries()
    {
        Clutter();
        Assert.Throws<UnsafeOperationException>(() => Prune().Plan("vault"));
        var plan = Prune().Plan("cloud");
        var edited = plan with { Files = [.. plan.Files, new("one.mp4", 12, _now.AddDays(-60), ReplicaPrune.Unreferenced)] };
        Assert.Throws<InvalidDataException>(() => Prune().Apply(edited));
        Assert.Throws<InvalidDataException>(() => Prune().Apply(plan with { Files = [new("../escape.mp4", 1, _now, ReplicaPrune.Unreferenced)] }));
        _store.Update(r => Ownership.Acquire(r, _first, SessionCoordinator.NewId(), DateTimeOffset.UtcNow), TimeSpan.FromSeconds(1));
        var before = Tree();
        Assert.Throws<UnsafeOperationException>(() => Prune().Apply(plan));
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void Prune_ALaunchOnThisPcStopsBeforeMoving()
    {
        Clutter();
        var plan = Prune().Plan("cloud");
        _idle = false;
        var before = Tree();
        Assert.Throws<OperationCanceledException>(() => Prune().Apply(plan));
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void PurgeTrash_DeletesOnlyBatchesOlderThanTheLimit()
    {
        Clutter();
        var plan = Prune().Plan("cloud");
        Prune().Apply(plan);
        var batch = Assert.Single(Prune().Trash("cloud"));
        Assert.Equal((plan.Id, 2), (batch.Name, batch.Files));
        Assert.Empty(Prune().Purge("cloud", ReplicaPrune.DefaultPurgeDays, apply: true));
        Assert.True(Directory.Exists(Path.Combine(Destination, ".medal-sync-trash", plan.Id)));

        Remote(".medal-sync-trash/undated/keep.mp4", "no date in name", _now.AddDays(-400));
        _now = _now.AddDays(8);
        var due = Assert.Single(Prune().Purge("cloud", ReplicaPrune.DefaultPurgeDays, apply: false));
        Assert.Equal(plan.Id, due.Name);
        Assert.True(Directory.Exists(Path.Combine(Destination, ".medal-sync-trash", plan.Id)));
        Prune().Purge("cloud", ReplicaPrune.DefaultPurgeDays, apply: true);
        Assert.False(Directory.Exists(Path.Combine(Destination, ".medal-sync-trash", plan.Id)));
        Assert.True(File.Exists(Path.Combine(Destination, ".medal-sync-trash", "undated", "keep.mp4")));
        Assert.True(File.Exists(Path.Combine(Destination, "one.mp4")));
    }
}

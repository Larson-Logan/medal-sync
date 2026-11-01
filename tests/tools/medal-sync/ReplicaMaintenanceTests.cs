using System.Runtime.Versioning;
using System.Text.Json;
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

/// <summary>A synthetic destination: a local folder per remote path, with optional failures injected.</summary>
internal sealed class FolderRemote(string root) : IReplicaRemote
{
    public Func<RemoteCheckOutcome?>? OnCheck { get; set; }
    public int? FailMoveAfter { get; set; }
    public int Moves { get; private set; }
    public int ChecksFinished { get; private set; }
    public List<string> Listings { get; } = [];
    public List<string> Purged { get; } = [];

    public string Folder(string destination) => Path.Combine([root, .. destination.Replace(":", "/", StringComparison.Ordinal).Split('/')]);

    public IReadOnlyList<RemoteFile> List(string destination)
    {
        Listings.Add(destination);
        string folder = Folder(destination);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => new RemoteFile(Path.GetRelativePath(folder, f).Replace('\\', '/'), new FileInfo(f).Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero)))
            .ToArray();
    }

    public RemoteCheckOutcome Check(string source, string destination, IReadOnlyList<string> files, Action? guard = null)
    {
        if (OnCheck?.Invoke() is { } injected) return injected;
        guard?.Invoke(); // The real remote polls the guard while rclone reads.
        var results = new Dictionary<string, RemoteCheck>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string there = Path.Combine(Folder(destination), file.Replace('/', '\\'));
            results[file] = !File.Exists(there) ? RemoteCheck.Missing
                : File.ReadAllBytes(there).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(source, file.Replace('/', '\\')))) ? RemoteCheck.Match
                : RemoteCheck.Differs;
        }
        ChecksFinished++;
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

    public void Mark(string destination, string folder, string name)
    {
        string path = Path.Combine(Folder(destination), folder.Replace('/', Path.DirectorySeparatorChar), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    public void Purge(string destination, string folder)
    {
        Purged.Add(folder);
        Directory.Delete(Path.Combine(Folder(destination), folder.Replace('/', '\\')), recursive: true);
    }
}

public sealed class ReplicaRemoteTests
{
    private static Dictionary<string, object> ListingEntry(string path = "private-clip.mp4") => new()
    {
        ["Path"] = path, ["Size"] = 42L, ["ModTime"] = "2026-10-01T12:34:56+02:00", ["IsDir"] = false, ["IsLink"] = false,
    };

    [Theory]
    [InlineData("private-clip.mp4", false)]
    [InlineData("PRIVATE-CLIP.mp4", false)]
    [InlineData("private-clip.mp4", true)]
    [InlineData("PRIVATE-CLIP.mp4", true)]
    public void ReplicaListing_RefusesDuplicateAndCaseConflictingPathsWithoutNamingFiles(string duplicate, bool directory)
    {
        var entry = ListingEntry();
        entry["IsDir"] = directory;
        string json = JsonSerializer.Serialize(new[] { entry, ListingEntry(duplicate) });
        var error = Assert.Throws<InvalidDataException>(() => RcloneReplicaRemote.Parse(json));
        Assert.Contains("rclone dedupe", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-clip", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../private-clip.mp4")]
    [InlineData("/private-clip.mp4")]
    [InlineData("folder//private-clip.mp4")]
    [InlineData("folder/./private-clip.mp4")]
    [InlineData("folder\\private-clip.mp4")]
    [InlineData("folder/private-clip.mp4 ")]
    [InlineData("folder/private-clip.mp4:stream")]
    [InlineData("folder/private-clip\n.mp4")]
    [InlineData("NUL.mp4")]
    [InlineData("")]
    public void ReplicaListing_RefusesUnsafePaths(string path)
    {
        var error = Assert.Throws<InvalidDataException>(() => RcloneReplicaRemote.Parse(JsonSerializer.Serialize(new[] { ListingEntry(path) })));
        Assert.DoesNotContain("private-clip", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Path", null)]
    [InlineData("Path", "17")]
    [InlineData("Size", null)]
    [InlineData("Size", "\"42\"")]
    [InlineData("Size", "-1")]
    [InlineData("ModTime", null)]
    [InlineData("ModTime", "17")]
    [InlineData("ModTime", "null")]
    [InlineData("ModTime", "\"invalid\"")]
    [InlineData("IsLink", "true")]
    [InlineData("IsLink", "\"false\"")]
    [InlineData("IsDir", "\"false\"")]
    public void ReplicaListing_RefusesMalformedEntriesAndLinks(string field, string? value)
    {
        var entry = ListingEntry();
        if (value is null) entry.Remove(field);
        else entry[field] = JsonSerializer.Deserialize<JsonElement>(value);
        var error = Assert.Throws<InvalidDataException>(() => RcloneReplicaRemote.Parse(JsonSerializer.Serialize(new[] { entry })));
        Assert.DoesNotContain("private-clip", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    public void ReplicaListing_RefusesMalformedDocuments(string json) =>
        Assert.Throws<InvalidDataException>(() => RcloneReplicaRemote.Parse(json));

    [Fact]
    public void ReplicaListing_ParsesFilesAndSkipsDirectories()
    {
        var directory = ListingEntry("folder");
        directory["IsDir"] = true;
        directory.Remove("Size");
        directory.Remove("ModTime");
        var files = RcloneReplicaRemote.Parse(JsonSerializer.Serialize(new[] { ListingEntry("z.mp4"), directory, ListingEntry("folder/a.mp4") }));
        Assert.Equal(["folder/a.mp4", "z.mp4"], files.Select(f => f.Path));
        Assert.All(files, f => { Assert.Equal(42, f.Size); Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 34, 56, TimeSpan.Zero), f.ModTime); });
        Assert.Empty(RcloneReplicaRemote.Parse("[]"));
    }

    [Fact]
    public void ReplicaCheck_ParsesCombinedOutcomesWithoutInventingResults()
    {
        var results = RcloneReplicaRemote.ParseCombined(["= same.mp4", "+ missing.mp4", "* different.mp4", "! error.mp4", "- destination-only.mp4", "", "unknown", "=folder/ignored.mp4"]);
        Assert.Equal(4, results.Count);
        Assert.Equal(RemoteCheck.Match, results["same.mp4"]);
        Assert.Equal(RemoteCheck.Missing, results["missing.mp4"]);
        Assert.Equal(RemoteCheck.Differs, results["different.mp4"]);
        Assert.Equal(RemoteCheck.Error, results["error.mp4"]);
        Assert.Empty(RcloneReplicaRemote.ParseCombined([]));
    }
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

    [Theory]
    [InlineData("preview")]
    [InlineData("apply")]
    [InlineData("purge")]
    public void AmbiguousReplicaListing_RefusesCleanupBeforeAnyRemoteMutation(string operation)
    {
        var plan = Prune().Plan("cloud");
        var commands = new List<string>();
        var remote = new RcloneReplicaRemote(_config, _state, RequireIdle, args =>
        {
            commands.Add(args[0]);
            Assert.Equal("lsjson", args[0]);
            Assert.DoesNotContain("--files-only", args);
            return """[{"Path":"private-clip.mp4","Size":1,"ModTime":"2026-01-01T00:00:00Z"},{"Path":"private-clip.mp4","Size":1,"ModTime":"2026-01-01T00:00:00Z"}]""";
        });
        var prune = new ReplicaPrune(_config, _state, _store, RequireIdle, remote, () => _now);
        var before = Tree();
        var error = Assert.Throws<InvalidDataException>(() =>
        {
            if (operation == "preview") prune.Plan("cloud");
            else if (operation == "apply") prune.Apply(plan);
            else prune.Purge("cloud", 7, apply: true);
        });
        Assert.Contains("rclone dedupe", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-clip", error.ToString(), StringComparison.Ordinal);
        Assert.Single(commands);
        Assert.Equal(before, Tree());
        Assert.False(Directory.Exists(Path.Combine(_state.Root, "replication", "prune")));
    }

    [Fact]
    public void PurgeTrash_MissingFolderIsEmptyAndOtherListingFailuresAreNotHidden()
    {
        var remote = new RcloneReplicaRemote(_config, _state, RequireIdle, args =>
        {
            Assert.Equal("lsjson", args[0]);
            Assert.EndsWith("/.medal-sync-trash", args[1], StringComparison.Ordinal);
            throw new RcloneFailedException("Synthetic directory missing", 3);
        });
        var prune = new ReplicaPrune(_config, _state, _store, RequireIdle, remote);
        Assert.Empty(prune.Purge("cloud", 7, apply: true));
        remote = new RcloneReplicaRemote(_config, _state, RequireIdle, _ => throw new RcloneFailedException("Synthetic sign-in failure", 1));
        prune = new ReplicaPrune(_config, _state, _store, RequireIdle, remote);
        Assert.Throws<RcloneFailedException>(() => prune.Purge("cloud", 7, apply: true));
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
        Assert.Equal(0, _remote.ChecksFinished);
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
        // The journal-aware guard stops the comparison itself, not only its result.
        Assert.Equal(0, _remote.ChecksFinished);
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
    public void Prune_ARecentAbandonedAttemptBelowTheHeadStillProtectsItsFiles()
    {
        Clutter();
        // An attempt that referenced old.mp4 was abandoned; a later switch without it committed a higher number.
        MedalFixture.AddContent(_db, "old", video: @"M:\Clips\old.mp4");
        long head = _store.ReadJournal().CommittedGeneration;
        long abandoned = head + 1;
        _store.Generations.Publish(abandoned, head, SessionCoordinator.NewId(), _first, LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId),
            new(1, [], []), Versions, _now.AddDays(-1));
        MedalFixture.DeleteContent(_db, "old");
        Commit(_now);
        Assert.True(_store.ReadJournal().CommittedGeneration > abandoned);
        Assert.DoesNotContain(Prune().Plan("cloud").Files, f => f.Path == "old.mp4");
        // Once that attempt is older than the grace period it no longer protects anything.
        _now = _now.AddDays(20);
        Assert.Contains(Prune().Plan("cloud").Files, f => f.Path == "old.mp4");
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
    public void PurgeTrash_CountsAgeFromTheMoveNotTheReview()
    {
        Clutter();
        var plan = Prune().Plan("cloud");
        _now = _now.AddDays(10);
        Prune().Apply(plan);
        var batch = Assert.Single(Prune().Trash("cloud"));
        Assert.Equal(_now, batch.CreatedAt!.Value, TimeSpan.FromMilliseconds(1)); // The marker keeps milliseconds.
        Assert.Empty(Prune().Purge("cloud", ReplicaPrune.DefaultPurgeDays, apply: true));
        Assert.True(File.Exists(Path.Combine(Destination, ".medal-sync-trash", plan.Id, "old.mp4")));
    }

    [Fact]
    public void PurgeTrash_ListsOnlyTheTrashSubtreeOnce()
    {
        Assert.Empty(Prune().Purge("cloud", ReplicaPrune.DefaultPurgeDays, apply: true));
        Assert.Equal("synth:Medal/" + _config.LibraryId + "/.medal-sync-trash", Assert.Single(_remote.Listings));
        Assert.Empty(_remote.Purged);
    }

    private void TrashBatch(string name, DateTimeOffset? moved)
    {
        Remote(ReplicaPrune.TrashRoot + "/" + name + "/old.mp4", "trashed", _now.AddDays(-60));
        if (moved is { } at)
            Remote(ReplicaPrune.TrashRoot + "/" + name + "/" + ReplicaPrune.MovedMarker
                + at.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture), "", _now);
    }

    private sealed class ReportingWriter(Action onReport) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            onReport();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PurgeTrash_DeletesExactlyTheReportedBatchesUsingOneListing()
    {
        if (!OperatingSystem.IsWindows()) return;
        TrashBatch("old", _now.AddDays(-8));
        TrashBatch("recent", _now.AddDays(-7).AddMinutes(1));
        TrashBatch("undated", null);
        bool changed = false;
        using var output = new ReportingWriter(() =>
        {
            if (changed) return;
            changed = true;
            _now = _now.AddDays(1);
            TrashBatch("newly-discovered", _now.AddDays(-10));
        });
        Assert.Equal(0, ReplicaCommands.PurgeTrash(Prune(), output, "cloud", ReplicaPrune.DefaultPurgeDays, apply: true));
        Assert.Single(_remote.Listings);
        Assert.Equal([".medal-sync-trash/old"], _remote.Purged);
        Assert.Contains("old: 1 file(s),", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("  deleting", output.ToString(), StringComparison.Ordinal);
        foreach (string kept in new[] { "recent", "undated" })
            Assert.Contains(output.ToString().Split('\n'), line => line.StartsWith("  " + kept + ":", StringComparison.Ordinal)
                && line.TrimEnd().EndsWith("  kept", StringComparison.Ordinal));
        Assert.DoesNotContain("newly-discovered", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deleted 1 trash batch(es)", output.ToString(), StringComparison.Ordinal);
        foreach (string kept in new[] { "recent", "undated", "newly-discovered" })
            Assert.True(Directory.Exists(Path.Combine(Destination, ReplicaPrune.TrashRoot, kept)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void PurgeTrash_PreviewAndEmptyTrashReportWithoutDeleting(bool empty)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!empty) TrashBatch("old", _now.AddDays(-8));
        using var output = new StringWriter();
        ReplicaCommands.PurgeTrash(Prune(), output, "cloud", ReplicaPrune.DefaultPurgeDays, apply: false);
        Assert.Single(_remote.Listings);
        Assert.Empty(_remote.Purged);
        Assert.Contains(empty ? "The trash is empty." : "Preview only", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PurgeTrash_CancellationAfterReportingKeepsEveryBatch()
    {
        TrashBatch("old", _now.AddDays(-8));
        Assert.Throws<OperationCanceledException>(() => Prune().Purge("cloud", 7, apply: true, report: (_, _) => _idle = false));
        Assert.Single(_remote.Listings);
        Assert.Empty(_remote.Purged);
        Assert.True(Directory.Exists(Path.Combine(Destination, ReplicaPrune.TrashRoot, "old")));
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

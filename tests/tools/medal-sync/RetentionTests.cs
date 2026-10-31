using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary><c>retention</c>: reviewed removal of old generations and this PC's recovery folders while Ready.</summary>
public sealed class RetentionTests : IDisposable
{
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly MedalFixture _fixture = new();
    private readonly DeviceIdentity _other = new(new string('b', 32), "Other PC");
    private readonly DeviceIdentity _me;
    private readonly MedalSyncConfig _config;
    private readonly LocalState _state;
    private readonly CoordinationStore _store;
    private readonly string _db;

    public RetentionTests()
    {
        _state = new(_fixture.PathFor("state"));
        _me = _state.Device();
        _config = new(1, new string('a', 32), MedalFixture.AccountId, _fixture.PathFor("coordination"),
            _fixture.PathFor("media"), @"M:\Clips", _fixture.PathFor("cache"), @"C:\Tools\rclone.exe",
            new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []) { StorageMode = StorageMode.Local };
        _db = _fixture.CreateLibrary("seed");
        _store = CoordinationStore.Create(_config.LibraryCoordinationDirectory, _config.LibraryId, Snapshot(), new(1, [], []), _me,
            SessionCoordinator.NewId(), Versions, Now.AddDays(-400));
    }

    public void Dispose() => _fixture.Dispose();

    private LibrarySnapshot Snapshot() => LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId);
    private Retention Retention() => new(_config, _state, _me, _store, () => Now);
    private GenerationStore Generations => _store.Generations;
    private bool Folder(long generation) => Generations.Exists(generation);

    private long Commit(DeviceIdentity device, DateTimeOffset at)
    {
        string session = SessionCoordinator.NewId();
        void Change(Func<OwnershipRecord, OwnershipRecord> t) => _store.Update(t, TimeSpan.FromSeconds(1));
        Change(r => Ownership.Acquire(r, device, session, at));
        Change(r => Ownership.MarkActive(r, device, session, at));
        Change(r => Ownership.BeginDrain(r, device, session, at));
        Change(r => Ownership.BeginPublish(r, device, session, at));
        long committed = _store.ReadJournal().CommittedGeneration;
        long next = Generations.Next(committed);
        MedalFixture.AddContent(_db, "clip-" + next, video: $@"M:\Clips\clip-{next}.mp4");
        Generations.Publish(next, committed, session, device, Snapshot(), new(1, [], []), Versions, at);
        Change(r => Ownership.CompletePublish(r, device, session, next, at));
        return next;
    }

    private long Abandon(DateTimeOffset at)
    {
        long head = _store.ReadJournal().CommittedGeneration;
        long number = Generations.Next(head);
        Generations.Publish(number, head, SessionCoordinator.NewId(), _me, Snapshot(), new(1, [], []), Versions, at);
        return number;
    }

    /// <summary>0..10 and 12..14 by this PC and 11 by the other PC, all a year old; head 14.</summary>
    private void LongHistory()
    {
        for (int i = 1; i <= 14; i++) Commit(i == 11 ? _other : _me, Now.AddDays(-380 + i));
    }

    private static RetentionPolicy Policy(int keep = 2) => new(keep, 90, 30);

    [Fact]
    public void Plan_KeepsTheHeadItsNewestAncestorsAndEachPcsLatestGeneration()
    {
        LongHistory();
        var plan = Retention().Plan(Policy());
        var generations = plan.Items.Where(i => i.Kind == Platform.Retention.Generation).Select(i => i.Generation!.Value).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, 11).Select(i => (long)i), generations);
        Assert.Equal(14, plan.Head);
        Assert.Equal(Enumerable.Range(0, 15).Reverse().Select(i => (long)i), plan.Ancestry);
        Assert.Contains("generations 0-10", Platform.Retention.Render(plan), StringComparison.Ordinal);
        Assert.Contains("--replace-local-library", Platform.Retention.Render(plan), StringComparison.Ordinal);
        // Planning is read-only.
        Assert.All(Enumerable.Range(0, 15), g => Assert.True(Folder(g)));
        Assert.Null(Generations.ReadRetention());
    }

    [Fact]
    public void Plan_KeepsEverythingNewerThanTheAgeLimit()
    {
        for (int i = 1; i <= 6; i++) Commit(_me, Now.AddDays(-10 + i));
        Assert.DoesNotContain(Retention().Plan(Policy(keep: 0)).Items, i => i.Kind == Platform.Retention.Generation && i.Generation > 0);
        Assert.Empty(Retention().Plan(new RetentionPolicy(0, 3650, 30)).Items);
    }

    [Fact]
    public void Apply_RemovesReviewedGenerationsRecordsThemAndHistoryShowsTheGapAsRetention()
    {
        LongHistory();
        var plan = Retention().Plan(Policy());
        string receipt = Retention().Apply(plan);
        Assert.True(File.Exists(receipt));
        Assert.All(Enumerable.Range(0, 11), g => Assert.False(Folder(g)));
        Assert.All(Enumerable.Range(11, 4), g => Assert.True(Folder(g)));
        Assert.Empty(Directory.EnumerateDirectories(Generations.Directory, ".trash-*"));

        var record = Generations.ReadRetention()!;
        Assert.Equal(14, record.HighWater);
        Assert.Equal(plan.Ancestry, record.Ancestry);
        Assert.All(record.Pruned, p => Assert.True(p.Committed));
        Assert.Equal(11, record.Pruned.Count);

        // The committed chain is still followed across the gap, which the unshared-changes check relies on.
        var chain = Generations.Chain(14).ToArray();
        Assert.Equal(plan.Ancestry, chain.Select(c => c.Generation));
        Assert.All(chain.Where(c => c.Generation <= 10), c => Assert.Null(c.Manifest));

        var list = GenerationHistory.List(Generations, 14, GenerationHistory.DefaultLimit, null, Now);
        Assert.True(list.Complete);
        Assert.Equal([14L, 13, 12, 11], list.Entries.Select(e => e.Generation));
        var range = Assert.Single(list.Pruned!);
        Assert.Equal((0L, 10L), (range.From, range.To));
        Assert.Contains("Generations 0-10 were removed by retention", GenerationHistory.Render(list), StringComparison.Ordinal);
        var diff = Assert.Throws<InvalidDataException>(() => GenerationHistory.Diff(Generations, 14, 5, 14, Now));
        Assert.Contains("removed by retention", diff.Message, StringComparison.Ordinal);
        Assert.True(GenerationHistory.Diff(Generations, 14, 12, 14, Now).Diff.FromIsAncestor);

        // Numbers keep increasing after pruning, and a new publish follows the pruned chain.
        Assert.Equal(15, Generations.Next(14));
        long next = Commit(_me, Now);
        Assert.Equal(15, next);
        Assert.Equal(Enumerable.Range(0, 16).Reverse().Select(i => (long)i), Generations.Chain(15).Select(c => c.Generation));
        Assert.Equal([12L], Retention().Plan(Policy()).Items.Where(i => i.Kind == Platform.Retention.Generation).Select(i => i.Generation!.Value));
    }

    [Fact]
    public void NumberingStaysAboveTheHighWaterMarkEvenWhenFoldersAreGone()
    {
        Commit(_me, Now.AddDays(-200));
        Generations.WriteRetention(new(GenerationRetention.CurrentFormat, 40, [1, 0], []));
        Assert.Equal(41, Generations.Next(1));
    }

    [Fact]
    public void AbandonedAttemptsAndStagingFoldersAreRemovedOnlyAfterTheGracePeriod_AndTheHighestFolderIsKept()
    {
        Commit(_me, Now.AddDays(-300));
        long stale = Abandon(Now.AddDays(-40));
        long recent = Abandon(Now.AddDays(-5));
        long highest = Abandon(Now.AddDays(-200));
        string oldStaging = Path.Combine(Generations.Directory, ".staging-0000000099-old");
        string newStaging = Path.Combine(Generations.Directory, ".staging-0000000099-new");
        Directory.CreateDirectory(oldStaging);
        Directory.CreateDirectory(newStaging);
        Directory.SetLastWriteTimeUtc(oldStaging, Now.AddDays(-40).UtcDateTime);

        var plan = Retention().Plan(Policy(keep: 20));
        Assert.Equal([stale], plan.Items.Where(i => i.Kind == Platform.Retention.Abandoned).Select(i => i.Generation!.Value));
        Assert.Equal([".staging-0000000099-old"], plan.Items.Where(i => i.Kind == Platform.Retention.Staging).Select(i => i.Name));

        Retention().Apply(plan);
        Assert.False(Folder(stale));
        Assert.True(Folder(recent));
        Assert.True(Folder(highest));
        Assert.False(Directory.Exists(oldStaging));
        Assert.True(Directory.Exists(newStaging));
        var pruned = Assert.Single(Generations.ReadRetention()!.Pruned);
        Assert.False(pruned.Committed);
        Assert.Equal(highest + 1, Generations.Next(_store.ReadJournal().CommittedGeneration));
    }

    [Fact]
    public void Apply_RefusesWhenTheLibraryChangedSinceTheReview()
    {
        LongHistory();
        var plan = Retention().Plan(Policy());
        Commit(_me, Now);
        Assert.Throws<UnsafeOperationException>(() => Retention().Apply(plan));
        Assert.All(Enumerable.Range(0, 16), g => Assert.True(Folder(g)));
        Assert.Null(Generations.ReadRetention());
    }

    [Fact]
    public void Apply_RefusesEditedPlansAndPlansFromAnotherPc()
    {
        LongHistory();
        var plan = Retention().Plan(Policy());
        var head = new RetentionItem(Platform.Retention.Generation, GenerationStore.Name(14), 14, Now, 0);
        Assert.Throws<InvalidDataException>(() => Retention().Apply(plan with { Items = [.. plan.Items, head] }));
        Assert.Throws<InvalidDataException>(() => Retention().Apply(plan with { Policy = Policy(keep: 0) }));
        var other = new Retention(_config, _state, _other, _store, () => Now);
        Assert.Throws<UnsafeOperationException>(() => other.Apply(plan));
        Assert.All(Enumerable.Range(0, 15), g => Assert.True(Folder(g)));
    }

    [Fact]
    public void Apply_HoldsTheWriterLockSoNoPcCanStartMeanwhile()
    {
        LongHistory();
        var plan = Retention().Plan(Policy());
        bool checkedLock = false;
        Retention().Apply(plan, () =>
        {
            if (checkedLock) return;
            checkedLock = true;
            Assert.Throws<StorageBusyException>(() =>
                _store.Update(r => Ownership.Acquire(r, _other, SessionCoordinator.NewId(), Now), TimeSpan.FromMilliseconds(300)));
        });
        Assert.True(checkedLock);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void Apply_RefusesWhileAnotherPcHoldsTheLibrary()
    {
        LongHistory();
        var plan = Retention().Plan(Policy());
        _store.Update(r => Ownership.Acquire(r, _other, SessionCoordinator.NewId(), Now), TimeSpan.FromSeconds(1));
        Assert.Throws<UnsafeOperationException>(() => Retention().Apply(plan));
        Assert.Throws<UnsafeOperationException>(() => Retention().Plan(Policy()));
        Assert.All(Enumerable.Range(0, 15), g => Assert.True(Folder(g)));
    }

    [Fact]
    public void AnInterruptedApplyLeavesWholeFoldersAndFinishesWhenAppliedAgain()
    {
        LongHistory();
        var plan = Retention().Plan(Policy());
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => Retention().Apply(plan, () =>
        {
            if (++calls == 4) throw new OperationCanceledException("Synthetic launch on this PC.");
        }));
        // Three folders moved into the trash; every remaining generation folder is whole.
        Assert.Equal(8, Enumerable.Range(0, 11).Count(g => Folder(g)));
        Assert.Single(Directory.EnumerateDirectories(Generations.Directory, ".trash-*"));
        var partial = GenerationHistory.List(Generations, 14, GenerationHistory.DefaultLimit, null, Now);
        Assert.True(partial.Complete);

        Retention().Apply(plan);
        Assert.All(Enumerable.Range(0, 11), g => Assert.False(Folder(g)));
        Assert.Empty(Directory.EnumerateDirectories(Generations.Directory, ".trash-*"));
    }

    [Fact]
    public void LocalRecoveryFoldersKeepTheNewestAndAnyWhoseCacheStillHoldsFiles()
    {
        Commit(_me, Now.AddDays(-300));
        string Local(string parent, string name, int ageDays)
        {
            string folder = Path.Combine(parent, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "evidence.json"), "{}");
            Directory.SetCreationTimeUtc(folder, Now.AddDays(-ageDays).UtcDateTime);
            return folder;
        }
        var reviews = Enumerable.Range(1, 4).Select(i => Local(_state.BackupDirectory, "accepted-changes-" + i, 100 + i)).ToArray();
        string recentReview = Local(_state.BackupDirectory, "accepted-changes-new", 3);
        string aborted = Path.Combine(_state.Root, "aborted-starts");
        string fullCache = _fixture.PathFor("cache", "full");
        Directory.CreateDirectory(fullCache);
        File.WriteAllText(Path.Combine(fullCache, "unpublished.mp4"), "recording");
        string emptyCache = _fixture.PathFor("cache", "empty");
        Directory.CreateDirectory(emptyCache);
        string inUse = Local(aborted, new string('1', 32), 200);
        File.WriteAllText(Path.Combine(inUse, "recovery.json"), $$"""{"cache":{{System.Text.Json.JsonSerializer.Serialize(fullCache)}}}""");
        string done = Local(aborted, new string('2', 32), 200);
        File.WriteAllText(Path.Combine(done, "recovery.json"), $$"""{"cache":{{System.Text.Json.JsonSerializer.Serialize(emptyCache)}}}""");
        string unreadable = Local(aborted, new string('3', 32), 200);
        File.WriteAllText(Path.Combine(unreadable, "recovery.json"), "{");
        foreach (string folder in new[] { inUse, done, unreadable }) Directory.SetCreationTimeUtc(folder, Now.AddDays(-200).UtcDateTime);

        var plan = Retention().Plan(new RetentionPolicy(1, 90, 30));
        var local = plan.Items.Where(i => !i.OnServer).Select(i => i.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Backups/accepted-changes-1", "Backups/accepted-changes-2", "Backups/accepted-changes-3", "Backups/accepted-changes-4" }
            .Append("aborted-starts/" + new string('2', 32)).Order(StringComparer.Ordinal), local);

        Retention().Apply(plan);
        Assert.All(reviews, f => Assert.False(Directory.Exists(f)));
        Assert.True(Directory.Exists(recentReview));
        Assert.True(Directory.Exists(inUse));
        Assert.True(Directory.Exists(unreadable));
        Assert.False(Directory.Exists(done));
        Assert.False(Directory.Exists(Path.Combine(_state.Root, "retention-trash", plan.Id)));
        Assert.Equal("recording", File.ReadAllText(Path.Combine(fullCache, "unpublished.mp4")));
    }

    [Fact]
    public void Plan_WarnsWhenAPcLastPublishedWithAVersionThatDoesNotUnderstandPrunedHistory()
    {
        long older = Commit(_other, Now.AddDays(-300));
        Commit(_me, Now.AddDays(-200));
        Assert.Empty(Retention().Plan(Policy()).Warnings);
        string manifestPath = Path.Combine(Generations.Directory, GenerationStore.Name(older), "manifest.json");
        var manifest = Json.ReadFile<GenerationManifest>(manifestPath, "manifest");
        File.WriteAllBytes(manifestPath, Json.Serialize(manifest with { ToolVersion = "1.2.0" }));
        var plan = Retention().Plan(Policy());
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("Other PC last published with medal-sync 1.2.0", warning, StringComparison.Ordinal);
        Assert.Contains("Warning: Other PC", Platform.Retention.Render(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void RangesCollapseConsecutiveNumbers() =>
        Assert.Equal("0-2, 5, 7-8", Platform.Retention.Ranges([0, 1, 2, 5, 7, 8]));
}

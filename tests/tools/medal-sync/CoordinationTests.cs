using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

public sealed class CoordinationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceIdentity First = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "First PC");
    private static readonly DeviceIdentity Second = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Second PC");
    private const string LibraryId = "0123456789abcdef0123456789abcdef";
    private const string SessionA = "11111111111111111111111111111111";
    private const string SessionB = "22222222222222222222222222222222";

    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private LibrarySnapshot Snapshot(params string[] ids)
    {
        string db = _fixture.CreateLibrary("pc-" + Guid.NewGuid().ToString("N"));
        foreach (string id in ids) MedalFixture.AddContent(db, id);
        return LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
    }

    private static readonly AssetManifest NoAssets = new(AssetManifest.CurrentFormat, [], []);
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");

    private CoordinationStore NewStore(params string[] ids) =>
        CoordinationStore.Create(_fixture.PathFor("server", LibraryId), LibraryId, Snapshot(ids), NoAssets, First, SessionA, Versions, Now);

    [Fact]
    public void Ownership_FollowsTheNormalSessionCycle()
    {
        var r = Ownership.Initial(LibraryId, MedalFixture.AccountId, Now);
        r = Ownership.Acquire(r, First, SessionA, Now);
        r = Ownership.MarkActive(r, First, SessionA, Now);
        r = Ownership.BeginDrain(r, First, SessionA, Now);
        Assert.Same(r, Ownership.BeginDrain(r, First, SessionA, Now));
        r = Ownership.BeginPublish(r, First, SessionA, Now);
        r = Ownership.CompletePublish(r, First, SessionA, 1, Now);

        Assert.Equal(OwnershipState.Clean, r.State);
        Assert.Null(r.Owner);
        Assert.Equal(1, r.CommittedGeneration);
        Assert.Equal(6, r.Revision);
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Active")]
    [InlineData("Draining")]
    [InlineData("Publishing")]
    [InlineData("RecoveryRequired")]
    public void Ownership_NeverHandsAnUnfinishedSessionToAnotherPc(string stateName)
    {
        var state = Enum.Parse<OwnershipState>(stateName);
        var dirty = Ownership.Initial(LibraryId, MedalFixture.AccountId, Now) with { State = state, Owner = First, SessionId = SessionA };

        Assert.Throws<OwnershipConflictException>(() => Ownership.Acquire(dirty, Second, SessionB, Now.AddDays(30)));
        Assert.Throws<OwnershipConflictException>(() => Ownership.BeginDrain(dirty, Second, SessionA, Now));
        Assert.Throws<OwnershipConflictException>(() => Ownership.RequireRecovery(dirty, Second, SessionA, "x", Now));
        Assert.Throws<OwnershipConflictException>(() => Ownership.ResumeActive(dirty, Second, SessionA, Now));
    }

    [Fact]
    public void Ownership_LetsOnlyTheSameSessionRecover()
    {
        var r = Ownership.MarkActive(Ownership.Acquire(Ownership.Initial(LibraryId, MedalFixture.AccountId, Now), First, SessionA, Now), First, SessionA, Now);
        r = Ownership.RequireRecovery(r, First, SessionA, "network lost", Now);

        Assert.Throws<OwnershipConflictException>(() => Ownership.ResumeActive(r, First, SessionB, Now));
        Assert.Equal(OwnershipState.Active, Ownership.ResumeActive(r, First, SessionA, Now).State);
        Assert.Equal(OwnershipState.Draining, Ownership.BeginDrain(r, First, SessionA, Now).State);
    }

    [Fact]
    public void Ownership_AbandonNeedsTheExactSessionAndAReason()
    {
        var r = Ownership.Acquire(Ownership.Initial(LibraryId, MedalFixture.AccountId, Now), First, SessionA, Now) with { CommittedGeneration = 4 };

        Assert.Throws<OwnershipConflictException>(() => Ownership.Abandon(r, Second, SessionB, "first PC lost", Now));
        Assert.Throws<OwnershipConflictException>(() => Ownership.Abandon(r, Second, SessionA, " ", Now));
        var abandoned = Ownership.Abandon(r, Second, SessionA, "first PC lost", Now);

        Assert.Equal(OwnershipState.Clean, abandoned.State);
        Assert.Equal(4, abandoned.CommittedGeneration);
        Assert.Contains(SessionA, abandoned.Reason, StringComparison.Ordinal);
        Assert.Throws<OwnershipConflictException>(() => Ownership.Abandon(abandoned, Second, SessionA, "again", Now));
    }

    [Fact]
    public void Ownership_CommitsOnlyANewerGeneration()
    {
        var r = Ownership.Initial(LibraryId, MedalFixture.AccountId, Now) with
        {
            State = OwnershipState.Publishing, Owner = First, SessionId = SessionA, CommittedGeneration = 3,
        };
        Assert.Throws<OwnershipConflictException>(() => Ownership.CompletePublish(r, First, SessionA, 2, Now));
        Assert.Throws<OwnershipConflictException>(() => Ownership.CompletePublish(r, First, SessionA, 3, Now));
        Assert.Equal(4, Ownership.CompletePublish(r, First, SessionA, 4, Now).CommittedGeneration);
        // 4 was left behind by an abandoned session, so this one is 5.
        Assert.Equal(5, Ownership.CompletePublish(r, First, SessionA, 5, Now).CommittedGeneration);
    }

    [Fact]
    public void Store_CreatesGenerationZeroAndReopens()
    {
        var created = NewStore("clip-1", "clip-2");
        var store = CoordinationStore.Open(created.Root, LibraryId, MedalFixture.AccountId);
        var journal = store.ReadJournal();
        var zero = store.Generations.Read(0);

        Assert.Equal(OwnershipState.Clean, journal.State);
        Assert.Equal(0, journal.CommittedGeneration);
        Assert.Equal(2, zero.Snapshot.RowCount("contents"));
        Assert.Equal(First, zero.Manifest.Device);
        Assert.Null(zero.Manifest.ParentGeneration);
    }

    [Fact]
    public void Store_RefusesToReinitializeOrOpenTheWrongPlace()
    {
        var store = NewStore();
        Assert.Throws<UnsafeOperationException>(() =>
            CoordinationStore.Create(store.Root, LibraryId, Snapshot(), NoAssets, First, SessionA, Versions, Now));
        // An unreachable or empty share must never look like an empty library.
        Assert.Throws<UnsafeOperationException>(() => CoordinationStore.Open(_fixture.PathFor("nowhere"), LibraryId, MedalFixture.AccountId));
        Assert.Throws<IncompatibleLibraryException>(() => CoordinationStore.Open(store.Root, "ffffffffffffffffffffffffffffffff", MedalFixture.AccountId));
        Assert.Throws<IncompatibleLibraryException>(() => CoordinationStore.Open(store.Root, LibraryId, "999"));
    }

    private static void CopyTree(string source, string target, Func<string, bool> include)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (!include(relative.Replace('\\', '/'))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
        }
    }

    private static string[] Listing(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p) + ":" + (File.Exists(p) ? Convert.ToHexString(File.ReadAllBytes(p)) : "dir"))
            .Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Store_RetriedInitAcceptsANewDisplayLabelForTheSameDevice()
    {
        var reference = NewStore("clip-a");
        string root = _fixture.PathFor("renamed-bootstrap", LibraryId);
        CopyTree(reference.Root, root, path => path != "library.json");
        var renamed = First with { DisplayName = "Desk PC" };
        var store = CoordinationStore.Create(root, LibraryId, Snapshot("clip-a"), NoAssets, renamed, SessionB, Versions, Now.AddHours(1));
        Assert.Equal(OwnershipState.Clean, store.ReadJournal().State);
        Assert.Equal(First.DeviceId, store.Generations.ReadManifest(0).Device.DeviceId);
        Assert.Equal(renamed, store.Identity.CreatedBy);
    }

    /// <summary>An interrupted init after the lock, after generation 0 (with a staging leftover), after a partial and a whole journal.</summary>
    [Theory]
    [InlineData("lock")]
    [InlineData("generation")]
    [InlineData("partial-journal")]
    [InlineData("journal")]
    public void Store_RetriedInitCompletesThisDevicesUnfinishedBootstrap(string step)
    {
        var reference = NewStore("clip-a");
        string root = _fixture.PathFor("interrupted-" + step, LibraryId);
        CopyTree(reference.Root, root, path => path == "writer.lock"
            || (step != "lock" && path.StartsWith("generations/0000000000/", StringComparison.Ordinal))
            || (step == "journal" && path == "journal.json"));
        if (step != "lock")
        {
            string staging = Path.Combine(root, "generations", ".staging-0000000000-" + new string('c', 32));
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "library.json"), "{");
        }
        byte[] journal = File.ReadAllBytes(Path.Combine(reference.Root, "journal.json"));
        if (step == "partial-journal") File.WriteAllBytes(Path.Combine(root, "journal.json"), journal[..(journal.Length / 2)]);

        var refusal = Assert.Throws<UnsafeOperationException>(() => CoordinationStore.Open(root, LibraryId, MedalFixture.AccountId));
        Assert.Contains("unfinished library initialization", refusal.Message);

        var store = CoordinationStore.Create(root, LibraryId, Snapshot("clip-a"), NoAssets, First, SessionB, Versions, Now.AddHours(1));
        var opened = CoordinationStore.Open(root, LibraryId, MedalFixture.AccountId);
        var record = opened.ReadJournal();
        Assert.Equal(OwnershipState.Clean, record.State);
        Assert.Equal(0, record.CommittedGeneration);
        Assert.Equal(store.Identity, opened.Identity);
        Assert.Equal(First, opened.Identity.CreatedBy);
        if (step == "lock") Assert.Equal(Now.AddHours(1), opened.Identity.CreatedAt);
        else
        {
            // Generation 0 is kept, never republished, and the journal and marker name its creation time.
            Assert.Equal(journal, File.ReadAllBytes(Path.Combine(root, "journal.json")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(reference.Root, "library.json")), File.ReadAllBytes(Path.Combine(root, "library.json")));
            Assert.Equal(SessionA, opened.Generations.ReadManifest(0).SessionId);
        }
        Assert.Throws<UnsafeOperationException>(() =>
            CoordinationStore.Create(root, LibraryId, Snapshot("clip-a"), NoAssets, First, SessionB, Versions, Now));
    }

    [Theory]
    [InlineData("other-device")]
    [InlineData("other-snapshot")]
    [InlineData("damaged-generation")]
    [InlineData("later-generation")]
    [InlineData("unknown-file")]
    [InlineData("journal-without-generation")]
    [InlineData("edited-journal")]
    [InlineData("marker")]
    public void Store_RetriedInitRefusesAForeignOrEditedFolderAndLeavesItUnchanged(string variant)
    {
        var reference = NewStore("clip-a");
        string root = _fixture.PathFor("foreign-" + variant, LibraryId);
        CopyTree(reference.Root, root, path => path == "writer.lock" || path.StartsWith("generations/0000000000/", StringComparison.Ordinal)
            || variant is "journal-without-generation" or "edited-journal" && path == "journal.json"
            || variant == "marker" && path == "library.json");
        var device = variant == "other-device" ? Second : First;
        var snapshot = variant == "other-snapshot" ? Snapshot("clip-b") : Snapshot("clip-a");
        switch (variant)
        {
            case "damaged-generation":
                File.AppendAllText(Path.Combine(root, "generations", "0000000000", "library.json"), " ");
                break;
            case "later-generation":
                Directory.CreateDirectory(Path.Combine(root, "generations", "0000000001"));
                break;
            case "unknown-file":
                File.WriteAllText(Path.Combine(root, "notes.txt"), "someone else's");
                break;
            case "journal-without-generation":
                Directory.Delete(Path.Combine(root, "generations"), recursive: true);
                break;
            case "edited-journal":
                File.WriteAllText(Path.Combine(root, "journal.json"), "{\"edited\":true}");
                break;
        }
        var before = Listing(root);
        Assert.Throws<UnsafeOperationException>(() =>
            CoordinationStore.Create(root, LibraryId, snapshot, NoAssets, device, SessionB, Versions, Now.AddHours(1)));
        Assert.Equal(before, Listing(root));
        Assert.False(variant != "marker" && File.Exists(Path.Combine(root, "library.json")));
    }

    /// <summary>Two PCs initialize the same empty folder: the second sees it empty, then the first fills it before the lock.</summary>
    [Theory]
    [InlineData("generation-and-journal")]
    [InlineData("complete-library")]
    [InlineData("claimed-library")]
    public void Store_FreshInitRevalidatesUnderTheLockWhenAnotherPcFilledTheFolder(string variant)
    {
        var reference = CoordinationStore.Create(_fixture.PathFor("other-pc", LibraryId), LibraryId, Snapshot("clip-b"), NoAssets, Second, SessionB, Versions, Now);
        if (variant == "claimed-library") reference.Update(r => Ownership.Acquire(r, Second, SessionB, Now), TimeSpan.FromSeconds(5));
        string root = _fixture.PathFor("raced", LibraryId);
        string[]? before = null;

        Assert.Throws<UnsafeOperationException>(() => CoordinationStore.Create(root, LibraryId, Snapshot("clip-a"), NoAssets, First, SessionA, Versions,
            Now.AddHours(1), beforeLock: () =>
            {
                CopyTree(reference.Root, root, path => variant != "generation-and-journal" || path != "library.json");
                before = Listing(root);
            }));

        Assert.Equal(before, Listing(root));
        if (variant != "generation-and-journal")
        {
            var opened = CoordinationStore.Open(root, LibraryId, MedalFixture.AccountId);
            Assert.Equal(Second, opened.Identity.CreatedBy);
            Assert.Equal(variant == "claimed-library" ? OwnershipState.Starting : OwnershipState.Clean, opened.ReadJournal().State);
        }
    }

    [Fact]
    public void Store_UpdateAppliesTransitionsUnderTheLock()
    {
        var store = NewStore();
        var started = store.Update(r => Ownership.Acquire(r, First, SessionA, Now), TimeSpan.FromSeconds(2));
        Assert.Equal(OwnershipState.Starting, store.ReadJournal().State);
        Assert.Equal(started, store.ReadJournal());

        Assert.Throws<OwnershipConflictException>(() => store.Update(r => Ownership.Acquire(r, Second, SessionB, Now), TimeSpan.FromSeconds(2)));
        Assert.Equal(started, store.ReadJournal());
        // A transition that skips revisions is a bug and is not written.
        Assert.Throws<InvalidOperationException>(() => store.Update(r => r with { Revision = r.Revision + 5 }, TimeSpan.FromSeconds(2)));
        Assert.Throws<InvalidDataException>(() => store.Update(r => r with { Revision = r.Revision + 1, CommittedGeneration = 9 }, TimeSpan.FromSeconds(2)));
        Assert.Equal(started, store.ReadJournal());
    }

    [Fact]
    public void Store_TimesOutInsteadOfWaitingForeverOnAHeldLock()
    {
        var store = NewStore();
        using (store.AcquireWriterLock(TimeSpan.FromSeconds(1)))
        {
            Assert.Throws<StorageBusyException>(() =>
                store.Update(r => Ownership.Acquire(r, Second, SessionB, Now), TimeSpan.FromMilliseconds(300)));
        }
        Assert.Equal(OwnershipState.Starting, store.Update(r => Ownership.Acquire(r, Second, SessionB, Now), TimeSpan.FromSeconds(1)).State);
    }

    [Fact]
    public void Store_KeepsOneRequestPerDeviceAndIgnoresJunk()
    {
        var store = NewStore();
        store.WriteRequest(new HandoffRequest(1, Second, SessionB, Now));
        store.WriteRequest(new HandoffRequest(1, Second, SessionB, Now.AddMinutes(1)));
        File.WriteAllText(Path.Combine(store.Root, "requests", "not-a-device.json"), "{}");
        File.WriteAllText(Path.Combine(store.Root, "requests", First.DeviceId + ".json"), "{ half written");

        var pending = store.PendingRequests(Now);

        Assert.Single(pending);
        Assert.Equal(Now.AddMinutes(1), pending[0].RequestedAt);
        store.RemoveRequest(Second);
        Assert.Empty(store.PendingRequests(Now));
    }

    [Fact]
    public void Generations_DetectDamageAndIgnorePartialPublishes()
    {
        var store = NewStore("clip-1");
        store.Generations.Publish(1, 0, SessionA, First, Snapshot("clip-1", "clip-2"), NoAssets, Versions, Now);
        Directory.CreateDirectory(Path.Combine(store.Generations.Directory, ".staging-0000000002-deadbeef"));
        Directory.CreateDirectory(Path.Combine(store.Generations.Directory, "0000000003-incomplete"));

        Assert.Equal([0L, 1L], store.Generations.Numbered());
        Assert.Throws<OwnershipConflictException>(() =>
            store.Generations.Publish(1, 0, SessionA, First, Snapshot(), NoAssets, Versions, Now));

        string library = Path.Combine(store.Generations.Directory, GenerationStore.Name(1), "library.json");
        byte[] bytes = File.ReadAllBytes(library);
        bytes[^2] ^= 0x01;
        File.WriteAllBytes(library, bytes);
        Assert.Throws<InvalidDataException>(() => store.Generations.Read(1));
        Assert.Equal(1, store.Generations.Read(0).Snapshot.RowCount("contents"));
    }

    [Fact]
    public void Generations_RefuseAnotherAccountsSnapshot()
    {
        var store = NewStore();
        var foreign = Snapshot();
        var relabeled = LibrarySnapshot.Create("777", foreign.SchemaVersion, foreign.SchemaFingerprint, foreign.Tables);
        Assert.Throws<IncompatibleLibraryException>(() =>
            store.Generations.Publish(1, 0, SessionA, First, relabeled, NoAssets, Versions, Now));
    }
}

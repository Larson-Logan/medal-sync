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

        var pending = store.PendingRequests();

        Assert.Single(pending);
        Assert.Equal(Now.AddMinutes(1), pending[0].RequestedAt);
        store.RemoveRequest(Second);
        Assert.Empty(store.PendingRequests());
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

using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class PersistentMediaRefreshTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private readonly DeviceIdentity _first = new(new string('a', 32), "First PC");
    private readonly DeviceIdentity _second = new(new string('b', 32), "Second PC");
    private readonly MedalSyncConfig _config;
    private readonly LocalState _state;
    private readonly CoordinationStore _store;
    private bool _idle = true;
    private bool _compatible = true;

    public PersistentMediaRefreshTests()
    {
        _config = new(1, new string('c', 32), MedalFixture.AccountId, _fixture.PathFor("coordination"),
            _fixture.PathFor("server"), @"M:\Clips", _fixture.PathFor("cache"), @"C:\Tools\rclone.exe",
            new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, [])
        { Replication = new(_fixture.PathFor("second-local"), null, []) { ConflictPolicy = ReplicaConflictPolicy.Version } };
        _state = new(_fixture.PathFor("second-state"));
        foreach (var root in new[] { _config.ServerCapture, _config.MountedCapture })
        {
            StorageIdentity.Initialize(root.Display, _config.LibraryId);
            File.WriteAllText(root.Append("one.mp4").Display, "old-one");
            File.WriteAllText(root.Append("two.mp4").Display, "old-two");
        }
        string db = _fixture.CreateLibrary("first-pc");
        MedalFixture.AddContent(db, "one", video: @"M:\Clips\one.mp4");
        MedalFixture.AddContent(db, "two", video: @"M:\Clips\two.mp4");
        var snapshot = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        var versions = new MedalVersions("2643.583.1", "2643.3040.1");
        _store = CoordinationStore.Create(_config.LibraryCoordinationDirectory, _config.LibraryId,
            snapshot, new(1, [], []), _first, SessionCoordinator.NewId(), versions, DateTimeOffset.UnixEpoch);
        // PC A changes the referenced bytes and hands off generation 1, retaining destination history.
        string session = SessionCoordinator.NewId();
        Change(r => Ownership.Acquire(r, _first, session, DateTimeOffset.UtcNow));
        Change(r => Ownership.MarkActive(r, _first, session, DateTimeOffset.UtcNow));
        foreach (string path in new[] { "one.mp4", "two.mp4" })
        {
            string prior = _config.ServerCapture.Append(".medal-sync-history", "published-attempt", path).Display;
            Directory.CreateDirectory(Path.GetDirectoryName(prior)!);
            File.Copy(_config.ServerCapture.Append(path).Display, prior);
            File.WriteAllText(_config.ServerCapture.Append(path).Display, "new-" + path);
        }
        Change(r => Ownership.BeginDrain(r, _first, session, DateTimeOffset.UtcNow));
        Change(r => Ownership.BeginPublish(r, _first, session, DateTimeOffset.UtcNow));
        _store.Generations.Publish(1, 0, session, _first, snapshot, new(1, [], []), versions, DateTimeOffset.UtcNow);
        Change(r => Ownership.CompletePublish(r, _first, session, 1, DateTimeOffset.UtcNow));
    }

    public void Dispose() => _fixture.Dispose();
    private void Change(Func<OwnershipRecord, OwnershipRecord> transition) => _store.Update(transition, TimeSpan.FromSeconds(1));
    private PersistentMediaRefresh Refresh(Action<string>? after = null, DeviceIdentity? device = null) =>
        new(_config, _state, device ?? _second, _store,
            () => { if (!_idle) throw new UnsafeOperationException("Synthetic Medal is running."); },
            () => { if (!_compatible) throw new IncompatibleLibraryException("Synthetic unsupported update."); }, after);

    [Fact]
    public void SequentialHandoffReviewsKnownPriorBytesAndPreservesBothLocalFiles()
    {
        var plan = Refresh().Preview();
        Assert.Equal(1, plan.Generation);
        Assert.All(plan.Files, file =>
        {
            Assert.True(file.Conflict);
            Assert.Equal("matches preserved server history", file.Classification);
            Assert.NotNull(file.PriorServerVersion);
        });
        string recovery = Refresh().Apply(plan, acceptUnknown: false);
        Assert.Equal("old-one", File.ReadAllText(Path.Combine(recovery, "previous", "one.mp4")));
        Assert.Equal("old-two", File.ReadAllText(Path.Combine(recovery, "previous", "two.mp4")));
        Assert.Equal("new-one.mp4", File.ReadAllText(_config.MountedCapture.Append("one.mp4").Display));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal(1, _store.ReadJournal().CommittedGeneration);
        Assert.True(File.Exists(Path.Combine(recovery, "review.json")));
    }

    [Fact]
    public void UnknownUnpublishedLocalBytesRequireSeparateExplicitApprovalAndAreBackedUp()
    {
        File.WriteAllText(_config.MountedCapture.Append("one.mp4").Display, "unpublished second-pc work");
        var plan = Refresh().Preview();
        var unknown = Assert.Single(plan.Files, f => f.PriorServerVersion is null);
        Assert.Contains("unknown local modification", unknown.Classification);
        Assert.Throws<UnsafeOperationException>(() => Refresh().Apply(plan, acceptUnknown: false));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal("unpublished second-pc work", File.ReadAllText(_config.MountedCapture.Append("one.mp4").Display));
        string recovery = Refresh().Apply(plan, acceptUnknown: true);
        Assert.Equal("unpublished second-pc work", File.ReadAllText(Path.Combine(recovery, "previous", "one.mp4")));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("server")]
    [InlineData("ownership")]
    [InlineData("device")]
    [InlineData("config")]
    public void ExactReviewedStateChangesRefuseBeforeOwnershipOrReplacement(string change)
    {
        var plan = Refresh().Preview();
        if (change == "local") File.WriteAllText(_config.MountedCapture.Append("one.mp4").Display, "changed");
        if (change == "server") File.WriteAllText(_config.ServerCapture.Append("one.mp4").Display, "changed");
        if (change == "ownership")
        {
            string session = SessionCoordinator.NewId();
            Change(r => Ownership.Acquire(r, _first, session, DateTimeOffset.UtcNow));
            Change(r => Ownership.AbortStart(r, _first, session, "synthetic", DateTimeOffset.UtcNow));
        }
        if (change == "device") plan = plan with { DeviceId = _first.DeviceId };
        if (change == "config") plan = plan with { ConfigDigest = new string('0', 64) };
        Assert.Throws<UnsafeOperationException>(() => Refresh().Apply(plan, acceptUnknown: true));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Equal("old-two", File.ReadAllText(_config.MountedCapture.Append("two.mp4").Display));
        Assert.False(Directory.Exists(_state.BackupDirectory));
    }

    [Fact]
    public void InterruptedRefreshRetainsOwnershipBlocksOtherPcAndRetriesSameReview()
    {
        var plan = Refresh().Preview();
        Assert.Throws<IOException>(() => Refresh(_ => throw new IOException("Synthetic power interruption")).Apply(plan, false));
        var journal = _store.ReadJournal();
        Assert.True(PersistentMediaRefresh.IsPending(journal));
        Assert.Equal(OwnershipState.Starting, journal.State);
        Assert.Equal(_second.DeviceId, journal.Owner!.DeviceId);
        Assert.Throws<OwnershipConflictException>(() => Change(r => Ownership.Acquire(r, _first, SessionCoordinator.NewId(), DateTimeOffset.UtcNow)));
        Assert.Throws<UnsafeOperationException>(() => Refresh(device: _first).Apply(plan, false));
        // A new process resumes the original operation, verifies the already-replaced file, and finishes the rest.
        string recovery = Refresh().Apply(Json.Deserialize<MediaRefreshPlan>(Json.Serialize(plan), "review"), false);
        Assert.Equal("old-one", File.ReadAllText(Path.Combine(recovery, "previous", "one.mp4")));
        Assert.Equal("old-two", File.ReadAllText(Path.Combine(recovery, "previous", "two.mp4")));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Change(r => Ownership.Acquire(r, _first, SessionCoordinator.NewId(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ChangedFileAfterInterruptionCannotBeSilentlyOverwrittenOnRetry()
    {
        var plan = Refresh().Preview();
        Assert.Throws<IOException>(() => Refresh(_ => throw new IOException("interrupted")).Apply(plan, false));
        File.WriteAllText(_config.MountedCapture.Append("two.mp4").Display, "new unpublished bytes");
        Assert.Throws<UnsafeOperationException>(() => Refresh().Apply(plan, true));
        Assert.True(PersistentMediaRefresh.IsPending(_store.ReadJournal()));
        Assert.Equal("new unpublished bytes", File.ReadAllText(_config.MountedCapture.Append("two.mp4").Display));
    }

    [Fact]
    public void PartialStageFromInterruptedCopyIsPreservedAndVerifiedRetryCompletes()
    {
        var plan = Refresh().Preview();
        Assert.Throws<IOException>(() => Refresh(_ => throw new IOException("interrupted")).Apply(plan, false));
        File.WriteAllText(_config.MountedCapture.Append("two.mp4.medal-sync-refresh-" + plan.Id).Display, "partial staged bytes");
        string recovery = Refresh().Apply(plan, false);
        string partial = Assert.Single(Directory.GetFiles(recovery, "partial-*"));
        Assert.Equal("partial staged bytes", File.ReadAllText(partial));
        Assert.Equal("new-two.mp4", File.ReadAllText(_config.MountedCapture.Append("two.mp4").Display));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void MidApplyEnvironmentChangeRetainsOwnershipAndDefersRemainingReplacement()
    {
        var plan = Refresh().Preview();
        Assert.Throws<UnsafeOperationException>(() => Refresh(_ => _idle = false).Apply(plan, false));
        Assert.True(PersistentMediaRefresh.IsPending(_store.ReadJournal()));
        Assert.Equal("old-two", File.ReadAllText(_config.MountedCapture.Append("two.mp4").Display));
        _idle = true;
        Refresh().Apply(plan, false);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void DamagedRetainedBackupBlocksRetryAndKeepsOwnership()
    {
        var plan = Refresh().Preview();
        Assert.Throws<IOException>(() => Refresh(_ => throw new IOException("interrupted")).Apply(plan, false));
        File.WriteAllText(Path.Combine(_state.BackupDirectory, "media-refresh-" + plan.Id, "previous", "one.mp4"), "damaged backup");
        Assert.Throws<IOException>(() => Refresh().Apply(plan, false));
        Assert.True(PersistentMediaRefresh.IsPending(_store.ReadJournal()));
        Assert.Equal("old-two", File.ReadAllText(_config.MountedCapture.Append("two.mp4").Display));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("files")]
    [InlineData("file")]
    [InlineData("server")]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("reserved")]
    [InlineData("duplicate")]
    public void MalformedDeserializedReviewsProduceControlledRefusals(string malformed)
    {
        var original = Refresh().Preview();
        var json = System.Text.Json.Nodes.JsonNode.Parse(Json.Serialize(original))!;
        var files = (System.Text.Json.Nodes.JsonArray)json["files"]!;
        switch (malformed)
        {
            case "id": json["id"] = null; break;
            case "files": json["files"] = null; break;
            case "file": files[0] = null; break;
            case "server": files[0]!["server"] = null; break;
            case "hash": files[0]!["server"]!["sha256"] = null; break;
            case "path": files[0]!["path"] = "../private"; break;
            case "reserved": files[0]!["path"] = "NUL.mp4"; break;
            default: files.Add(files[0]!.DeepClone()); break;
        }
        // Exercise JSON as used by the command, including explicit nulls despite C# annotations.
        var deserialized = Json.Deserialize<MediaRefreshPlan>(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "review");
        Assert.Throws<InvalidDataException>(() => Refresh().Apply(deserialized, true));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void ActiveOrIncompatibleMedalAndForeignStorageMarkersRefuse()
    {
        var plan = Refresh().Preview();
        _idle = false;
        Assert.Throws<UnsafeOperationException>(() => Refresh().Apply(plan, false));
        _idle = true;
        _compatible = false;
        Assert.Throws<IncompatibleLibraryException>(() => Refresh().Apply(plan, false));
        _compatible = true;
        File.WriteAllText(_config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display, new string('d', 32));
        Assert.Throws<UnsafeOperationException>(() => Refresh().Apply(plan, false));
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }
}

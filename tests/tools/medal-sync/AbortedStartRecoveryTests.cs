using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class AbortedStartRecoveryTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private readonly LocalState _state;
    private readonly MedalSyncConfig _config;
    private readonly CoordinationStore _store;
    private readonly string _session = new('b', 32);
    private bool _idle = true;

    public AbortedStartRecoveryTests()
    {
        _state = new(_fixture.PathFor("state"));
        _config = new(1, new string('a', 32), MedalFixture.AccountId, _fixture.PathFor("coordination"),
            _fixture.PathFor("media"), @"M:\Clips", _fixture.PathFor("cache"), @"C:\Tools\rclone.exe",
            new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []) { StorageMode = StorageMode.Local };
        string db = _fixture.CreateLibrary("seed");
        _store = CoordinationStore.Create(_config.LibraryCoordinationDirectory, _config.LibraryId,
            LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId), new(1, [], []), _state.Device(),
            SessionCoordinator.NewId(), new("2643.583.1", "2643.3040.1"), DateTimeOffset.UtcNow);
        File.WriteAllBytes(_state.ConfigPath, Json.Serialize(_config));
        _ = _state.SessionSecret(_session, DateTimeOffset.UtcNow);
    }
    public void Dispose() => _fixture.Dispose();
    private AbortedStartRecovery Recovery() => new(_config, _state, _store, () =>
    {
        if (!_idle) throw new UnsafeOperationException("Synthetic process/mount active");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderEmptyCacheOrphanNeedsExplicitApplyAndRetainsItsLogin(bool emptyCache)
    {
        string cache = _config.SessionCacheDirectory(_session);
        if (emptyCache) Directory.CreateDirectory(cache);
        var original = _state.SessionSecret(_session, DateTimeOffset.UtcNow);
        var journal = _store.ReadJournal();
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(_state.Root, false, false));
        Assert.Contains("Preview only", Recovery().Run(_session, false));
        Assert.False(Directory.Exists(Path.Combine(_state.Root, "aborted-starts")));
        Assert.Contains("retained", Recovery().Run(_session, true));
        Assert.Equal(original, new LocalState(_state.Root).SessionSecret(_session, DateTimeOffset.UtcNow));
        Assert.Equal(journal, _store.ReadJournal());
        Assert.Equal(emptyCache, Directory.Exists(cache));
        SetupMaintenance.RequireReady(_state.Root, false, false);
    }

    [Fact]
    public void NonemptyUnknownLegacyCacheRefusesPreviewAndApply()
    {
        string cache = _config.SessionCacheDirectory(_session);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "unknown"), "unpublished");
        Assert.Throws<UnsafeOperationException>(() => Recovery().Run(_session, false));
        Assert.Throws<UnsafeOperationException>(() => Recovery().Run(_session, true));
        Assert.Equal("unpublished", File.ReadAllText(Path.Combine(cache, "unknown")));
        Assert.True(File.Exists(Path.Combine(_state.Root, "sessions", _session + ".json")));
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(_state.Root, false, false));
    }

    [Theory]
    [InlineData((int)OwnershipState.Starting)]
    [InlineData((int)OwnershipState.Active)]
    [InlineData((int)OwnershipState.Draining)]
    [InlineData((int)OwnershipState.Publishing)]
    [InlineData((int)OwnershipState.RecoveryRequired)]
    public void OwnedOrUncertainSessionsCannotBeReclassifiedAsOrphans(int phase)
    {
        var device = _state.ExistingDevice();
        _store.Update(r => Ownership.Acquire(r, device, _session, DateTimeOffset.UtcNow) with { State = (OwnershipState)phase }, TimeSpan.FromSeconds(1));
        Assert.Throws<UnsafeOperationException>(() => Recovery().Run(_session, true));
        Assert.True(File.Exists(Path.Combine(_state.Root, "sessions", _session + ".json")));
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(_state.Root, false, false));
    }

    [Fact]
    public void ActiveProcessAndChangedIdleStateRefuseWithoutMovingCredentials()
    {
        _idle = false;
        Assert.Throws<UnsafeOperationException>(() => Recovery().Run(_session, true));
        Assert.True(File.Exists(Path.Combine(_state.Root, "sessions", _session + ".json")));
    }

    [Fact]
    public void CorruptCredentialsAndRetainedEvidenceRefuseWithRecoveryBytesPreserved()
    {
        string login = Path.Combine(_state.Root, "sessions", _session + ".json");
        File.WriteAllText(login, "{}");
        Assert.Throws<InvalidDataException>(() => Recovery().Run(_session, true));
        Assert.Equal("{}", File.ReadAllText(login));
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(_state.Root, false, false));
    }

    [Fact]
    public void DamagedRecoveryReceiptRefusesIdempotentCleanup()
    {
        string folder = _state.RetainAbortedStart(_session, _config.LibraryId, _config.SessionCacheDirectory(_session), DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(folder, "recovery.json"), "{}");
        Assert.Throws<InvalidDataException>(() => _state.RetainAbortedStart(_session, _config.LibraryId,
            _config.SessionCacheDirectory(_session), DateTimeOffset.UtcNow));
        Assert.True(File.Exists(Path.Combine(folder, "login.json")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(folder, "recovery.json")));
    }
}

using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>Idle prefetch of another PC's published clips into this PC's persistent local copy (#254).</summary>
public sealed class MediaPrefetchTests : IDisposable
{
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");
    private static readonly VolumeInfo Roomy = new(@"C:\", "NTFS:00000001", VolumeKind.Fixed, "NTFS", 1L << 40, 1L << 41);
    private readonly MedalFixture _fixture = new();
    private readonly DeviceIdentity _thisPc = new(new string('b', 32), "Living room");
    private readonly DeviceIdentity _otherPc = new(new string('a', 32), "Desk PC");
    private readonly MedalSyncConfig _config;
    private readonly LocalState _state;
    private readonly CoordinationStore _store;
    private readonly string _db;
    private readonly DateTime _start = DateTime.UtcNow.AddDays(-3);
    private readonly List<string> _commands = [];
    private Action<IReadOnlyList<string>>? _onCommand;
    private Action? _guard;
    private VolumeInfo _volume = Roomy;

    public MediaPrefetchTests()
    {
        string remote = _fixture.PathFor("server");
        _config = new MedalSyncConfig(1, new string('c', 32), MedalFixture.AccountId, _fixture.PathFor("coordination"), remote, @"M:\Clips",
            _fixture.PathFor("cache"), @"C:\Tools\rclone.exe", new(5, 20, 20, 300, 30, 1000, 2, 53684), 4, [])
        {
            Replication = new(_fixture.PathFor("persistent"), null, []),
        };
        _state = new LocalState(_fixture.PathFor("state"));
        StorageIdentity.Initialize(_config.ServerCapture.Display, _config.LibraryId);
        // This PC has launched before: its local copy is claimed and holds the older clip.
        Directory.CreateDirectory(_config.MountedCapture.Display);
        File.WriteAllText(_config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display, _config.LibraryId);
        _db = _fixture.CreateLibrary("library");
        Clip("old", 0, local: true);
        _store = Publish(firstGeneration: true);
    }

    public void Dispose() => _fixture.Dispose();

    /// <summary>A clip on primary storage, recorded <paramref name="hoursLater"/> after the start, and referenced by the library.</summary>
    private void Clip(string name, int hoursLater, bool local = false, string? content = null)
    {
        string bytes = content ?? "synthetic bytes of " + name;
        string server = _config.ServerCapture.Append(name + ".mp4").Display;
        File.WriteAllText(server, bytes);
        File.SetLastWriteTimeUtc(server, _start.AddHours(hoursLater));
        if (local) File.WriteAllText(_config.MountedCapture.Append(name + ".mp4").Display, bytes);
        MedalFixture.AddContent(_db, name, video: $@"M:\Clips\{name}.mp4");
    }

    private AssetManifest Manifest()
    {
        var snapshot = LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId);
        return new(1, ReplicationPlan.Media(snapshot, _config.Capture).Select(m =>
        {
            var file = new FileInfo(_config.ServerCapture.Append(m.Relative.Split('/')).Display);
            return new AssetEntry($@"M:\Clips\{m.Relative.Replace('/', '\\')}", file.Length, file.LastWriteTimeUtc);
        }).ToArray(), []);
    }

    /// <summary>The other PC publishes the current library as the next committed generation.</summary>
    private CoordinationStore Publish(bool firstGeneration = false)
    {
        var snapshot = LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId);
        if (firstGeneration)
            return CoordinationStore.Create(_config.LibraryCoordinationDirectory, _config.LibraryId, snapshot, Manifest(), _otherPc,
                SessionCoordinator.NewId(), Versions, DateTimeOffset.UtcNow.AddDays(-10));
        string session = SessionCoordinator.NewId();
        var now = DateTimeOffset.UtcNow;
        void Change(Func<OwnershipRecord, OwnershipRecord> t) => _store.Update(t, TimeSpan.FromSeconds(1));
        Change(r => Ownership.Acquire(r, _otherPc, session, now));
        Change(r => Ownership.MarkActive(r, _otherPc, session, now));
        Change(r => Ownership.BeginDrain(r, _otherPc, session, now));
        Change(r => Ownership.BeginPublish(r, _otherPc, session, now));
        long committed = _store.ReadJournal().CommittedGeneration;
        long next = _store.Generations.Next(committed);
        _store.Generations.Publish(next, committed, session, _otherPc, snapshot, Manifest(), Versions, now);
        Change(r => Ownership.CompletePublish(r, _otherPc, session, next, now));
        return _store;
    }

    /// <summary>rclone, emulated on local folders: copy the listed files, then byte-compare them.</summary>
    private void Rclone(IReadOnlyList<string> arguments)
    {
        _commands.Add(arguments[0]);
        _onCommand?.Invoke(arguments);
        var files = File.ReadAllLines(arguments[arguments.ToList().IndexOf("--files-from-raw") + 1]);
        foreach (string file in files)
        {
            string from = Path.Combine(arguments[1], file.Replace('/', '\\'));
            string to = Path.Combine(arguments[2], file.Replace('/', '\\'));
            if (arguments[0] == "copy")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(from, to);
            }
            else if (!File.ReadAllBytes(from).AsSpan().SequenceEqual(File.ReadAllBytes(to)))
                throw new RcloneFailedException("Synthetic difference found by check.", 1);
        }
    }

    private MediaPrefetch Prefetch(DeviceIdentity? device = null) =>
        new(_config, _state, _store, device ?? _thisPc, () => _guard?.Invoke(), Rclone, _ => _volume);

    private bool Local(string name) => File.Exists(_config.MountedCapture.Append(name + ".mp4").Display);

    private string[] LocalTree() => Directory.EnumerateFiles(_config.Replication!.LocalCopyRoot!, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(_config.Replication.LocalCopyRoot!, p)).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void AnIdlePcDownloadsTheOtherPcsNewClipsNewestFirstAndItsNextLaunchDownloadsNothing()
    {
        Clip("new-1", 10);
        Clip("new-2", 30);
        Publish();
        var before = _store.ReadJournal();
        var (_, plan) = Prefetch().Plan();
        Assert.Equal(["new-2.mp4", "new-1.mp4"], plan.Select(f => f.Relative));

        var result = Prefetch().Run();

        Assert.Equal((2, 0, (string?)null), (result.Downloaded, result.Remaining, result.Stopped));
        Assert.Equal("synthetic bytes of new-2", File.ReadAllText(_config.MountedCapture.Append("new-2.mp4").Display));
        Assert.True(Local("new-1"));
        Assert.Equal(before, _store.ReadJournal());
        Assert.False(Directory.Exists(Prefetch().Staging));
        Assert.Equal(["copy", "check"], _commands);

        _commands.Clear();
        var launch = new StorageReplication(_config, _state, () => { }, Rclone, volumes: _ => Roomy);
        launch.Prepare(_store.Generations.Read(_store.ReadJournal().CommittedGeneration).Snapshot);
        Assert.Empty(_commands);
        Assert.Empty(Prefetch().Plan().Files);
    }

    [Fact]
    public void AnotherPcHoldingTheLibraryStillLetsThisPcPrefetchButOwningItDoesNot()
    {
        Clip("new", 10);
        Publish();
        _store.Update(r => Ownership.Acquire(r, _otherPc, SessionCoordinator.NewId(), DateTimeOffset.UtcNow), TimeSpan.FromSeconds(1));
        Assert.Equal(1, Prefetch().Run().Downloaded);
        var refusal = Assert.Throws<UnsafeOperationException>(() => Prefetch(_otherPc).Plan());
        Assert.Contains("holds the library", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAlreadyInTheLocalCopyIsNeverReplaced()
    {
        Clip("new", 10);
        Publish();
        File.WriteAllText(_config.MountedCapture.Append("new.mp4").Display, "this PC's own different bytes");
        Assert.Empty(Prefetch().Plan().Files);
        Assert.Equal(0, Prefetch().Run().Downloaded);
        Assert.Equal("this PC's own different bytes", File.ReadAllText(_config.MountedCapture.Append("new.mp4").Display));
    }

    [Fact]
    public void AnInterruptionLeavesNoPartialFileAndTheNextRunFinishes()
    {
        Clip("new", 10);
        Publish();
        _onCommand = arguments =>
        {
            if (arguments[0] != "copy") return;
            // rclone was stopped part-way: half a file in staging, nothing in the local copy.
            string partial = Path.Combine(arguments[2], "new.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            File.WriteAllText(partial, "synthetic");
            throw new OperationCanceledException("Prefetch paused: this PC is in use.");
        };
        var stopped = Prefetch().Run();
        Assert.Equal((0, 1), (stopped.Downloaded, stopped.Remaining));
        Assert.Contains("in use", stopped.Stopped, StringComparison.Ordinal);
        Assert.False(Local("new"));
        Assert.False(Directory.Exists(Prefetch().Staging));

        _onCommand = null;
        Assert.Equal(1, Prefetch().Run().Downloaded);
        Assert.Equal("synthetic bytes of new", File.ReadAllText(_config.MountedCapture.Append("new.mp4").Display));
    }

    [Fact]
    public void InputOrALaunchBetweenDownloadAndMoveKeepsTheLocalCopyUnchanged()
    {
        Clip("new", 10);
        Publish();
        var before = LocalTree();
        _onCommand = arguments => { if (arguments[0] == "check") _guard = () => throw new OperationCanceledException("Cloud retry stopped for launch."); };
        var result = Prefetch().Run();
        Assert.Equal(0, result.Downloaded);
        Assert.Equal(before, LocalTree());
    }

    [Fact]
    public void ABytesMismatchOrAWrongSizeMovesNothing()
    {
        Clip("new", 10);
        Publish();
        _onCommand = arguments =>
        {
            if (arguments[0] != "check") return;
            File.WriteAllText(Path.Combine(arguments[2], "new.mp4"), "tampered during download");
        };
        Assert.Throws<RcloneFailedException>(() => Prefetch().Run());
        Assert.False(Local("new"));
        Assert.False(Directory.Exists(Prefetch().Staging));

        // Primary storage changed after the generation recorded the file's size.
        _onCommand = null;
        File.WriteAllText(_config.ServerCapture.Append("new.mp4").Display, "replaced on primary storage with other bytes");
        Assert.Throws<IOException>(() => Prefetch().Run());
        Assert.False(Local("new"));
    }

    [Fact]
    public void ANewerGenerationPublishedMidRunStopsIt()
    {
        Clip("new", 10);
        Publish();
        _onCommand = arguments => { if (arguments[0] == "copy" && _store.ReadJournal().CommittedGeneration == 1) { Clip("newer", 20); Publish(); } };
        var result = Prefetch().Run();
        Assert.Equal(0, result.Downloaded);
        Assert.Contains("newer library generation", result.Stopped, StringComparison.Ordinal);
        Assert.False(Local("new"));
    }

    [Fact]
    public void LowSpaceStopsBeforeDownloading()
    {
        Clip("new", 10);
        Publish();
        _volume = Roomy with { FreeBytes = 20L << 30 }; // Exactly the configured reserve.
        var result = Prefetch().Run();
        Assert.Equal((0, 1, "space"), (result.Downloaded, result.Remaining, result.Stopped));
        Assert.Empty(_commands);
    }

    [Fact]
    public void AGenerationThatFailsVerificationIsNeverPrefetchedFrom()
    {
        Clip("new", 10);
        Publish();
        File.WriteAllText(Path.Combine(_store.Generations.Directory, GenerationStore.Name(1), "library.json"), "{ damaged");
        Assert.ThrowsAny<Exception>(() => Prefetch().Run());
        Assert.Empty(_commands);
        Assert.False(Local("new"));
    }

    [Fact]
    public void AnUnclaimedLocalCopyIsLeftForTheFirstLaunch()
    {
        File.Delete(_config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display);
        Assert.Throws<UnsafeOperationException>(() => Prefetch().Plan());
    }

    [Fact]
    public void AStagingFolderThatOverlapsTheCaptureFolderIsRefusedBeforeAnythingIsRemoved()
    {
        Clip("new", 10);
        Publish();
        var overlap = new MedalSyncConfig(1, _config.LibraryId, MedalFixture.AccountId, _config.CoordinationRoot, _config.Remote,
            @"M:\.medal-sync-prefetch\Clips", _config.CacheRoot, @"C:\Tools\rclone.exe", _config.Mount, 4, []) { Replication = _config.Replication };
        string kept = overlap.MountedCapture.Append("kept.mp4").Display;
        Directory.CreateDirectory(overlap.MountedCapture.Display);
        File.WriteAllText(overlap.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display, overlap.LibraryId);
        File.WriteAllText(kept, "an existing clip");
        var prefetch = new MediaPrefetch(overlap, _state, _store, _thisPc, () => { }, Rclone, _ => _volume);

        Assert.Throws<UnsafeOperationException>(() => prefetch.Run());

        Assert.Equal("an existing clip", File.ReadAllText(kept));
        Assert.Empty(_commands);
    }

    [Theory]
    [InlineData(null, "2Mi")]
    [InlineData(1, "1Mi")]
    [InlineData(500, "2Mi")]
    public void PrefetchNeverUsesMoreThanTheBackgroundLimit(int? configured, string expected)
    {
        var limited = _config with { Replication = _config.Replication! with { BandwidthLimitMiB = configured } };
        Clip("new", 10);
        Publish();
        var limits = new List<string>();
        _onCommand = arguments => limits.Add(arguments[arguments.ToList().IndexOf("--bwlimit") + 1]);

        new MediaPrefetch(limited, _state, _store, _thisPc, () => { }, Rclone, _ => _volume).Run();

        Assert.Equal([expected, expected], limits);
    }

    [Fact]
    public void PrefetchIsOnWithALocalCopyUnlessTurnedOff()
    {
        Assert.True(MediaPrefetch.Enabled(_config));
        Assert.False(MediaPrefetch.Enabled(_config with { Replication = _config.Replication! with { PrefetchWhileIdle = false } }));
        Assert.False(MediaPrefetch.Enabled(_config with { Replication = null }));
        Assert.DoesNotContain("prefetchWhileIdle", System.Text.Encoding.UTF8.GetString(Json.Serialize(_config)), StringComparison.Ordinal);
    }
}

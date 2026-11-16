using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

public sealed class IdleMediaSyncTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
        public void Sleep(TimeSpan duration) => Now += duration;
    }
    private readonly Clock _clock = new();
    public void Dispose() => _fixture.Dispose();

    private (MedalSyncConfig Config, LocalState State, LibrarySnapshot Snapshot) Setup()
    {
        var config = new MedalSyncConfig(1, new string('a', 32), MedalFixture.AccountId,
            _fixture.PathFor("journal"), _fixture.PathFor("primary"), @"M:\Clips", _fixture.PathFor("cache"), "rclone.exe",
            new(5, 20, 20, 300, 30, 1000, 2, 53684), 4, [])
        { Replication = new(_fixture.PathFor("local"), null, []) };
        StorageIdentity.Initialize(config.ServerCapture.Display, config.LibraryId);
        StorageIdentity.Initialize(config.MountedCapture.Display, config.LibraryId);
        string db = _fixture.CreateLibrary("library");
        foreach (string name in new[] { "closed", "writing" })
        {
            MedalFixture.AddContent(db, name, video: $@"M:\Clips\{name}.mp4");
            string path = config.MountedCapture.Append(name + ".mp4").Display;
            File.WriteAllText(path, "synthetic bytes");
            File.SetLastWriteTimeUtc(path, _clock.Now.AddMinutes(-2).UtcDateTime);
        }
        return (config, new LocalState(_fixture.PathFor("state")), LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId));
    }

    [Fact]
    public void IdleCopySkipsAnOpenWriterAndFinishReusesVerifiedBytes()
    {
        var (config, state, snapshot) = Setup();
        List<string[]> copied = [];
        List<IReadOnlyList<string>> calls = [];
        void Execute(IReadOnlyList<string> args)
        {
            calls.Add(args.ToArray());
            string[] files = File.ReadAllLines(args[args.ToList().IndexOf("--files-from-raw") + 1]);
            if (args[0] != "copy") return;
            copied.Add(files);
            Assert.Equal("2Mi", args[args.ToList().IndexOf("--bwlimit") + 1]);
            Assert.Equal("1", args[args.ToList().IndexOf("--transfers") + 1]);
            foreach (string file in files) File.Copy(Path.Combine(args[1], file), Path.Combine(args[2], file), true);
        }
        var idle = new IdleMediaSync(config, state, _clock, Execute);
        Assert.False(idle.Sync(snapshot, () => { }));
        Assert.Empty(calls);
        _clock.Sleep(SettledMedia.SettleTime);
        string writing = config.MountedCapture.Append("writing.mp4").Display;
        using (var writer = new FileStream(writing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            Assert.False(idle.Sync(snapshot, () => { }));
            Assert.Equal(new[] { "closed.mp4" }, Assert.Single(copied));
            Assert.False(File.Exists(config.ServerCapture.Append("writing.mp4").Display));
        }
        Assert.False(idle.Sync(snapshot, () => { })); // A refused lease restarts settling.
        _clock.Sleep(SettledMedia.SettleTime);
        Assert.True(idle.Sync(snapshot, () => { }));
        Assert.Equal(new[] { "writing.mp4" }, copied[^1]);
        Assert.False(File.Exists(Path.Combine(state.Root, "replication", "last-verified.json")));
        calls.Clear();
        var result = new StorageReplication(config, state, () => { }, args => calls.Add(args.ToArray())).Publish(snapshot);
        Assert.Equal(0, result.ReadBackFiles);
        Assert.Contains("--size-only", Assert.Single(calls));
        Assert.Equal("synthetic bytes", File.ReadAllText(config.ServerCapture.Append("writing.mp4").Display));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SizeOrModificationChangesRestartSettling(bool grow)
    {
        string file = _fixture.PathFor("clip.mp4");
        File.WriteAllText(file, "synthetic");
        File.SetLastWriteTimeUtc(file, _clock.Now.AddMinutes(-2).UtcDateTime);
        var settled = new SettledMedia();
        Assert.Null(settled.TryLease(file, _clock.Now));
        _clock.Sleep(SettledMedia.SettleTime);
        if (grow) File.AppendAllText(file, " more");
        else File.SetLastWriteTimeUtc(file, _clock.Now.AddMinutes(-1).UtcDateTime);
        Assert.Null(settled.TryLease(file, _clock.Now));
        _clock.Sleep(SettledMedia.SettleTime);
        using var lease = settled.TryLease(file, _clock.Now);
        Assert.NotNull(lease);
        Assert.Throws<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        using var reader = File.OpenRead(file); // rclone can read while the write exclusion is held.
        Assert.True(reader.Length > 0);
    }

    [Fact]
    public void InterruptedIdleCopyKeepsSourceAndForcesVerificationOnRetry()
    {
        var (config, state, snapshot) = Setup();
        var idle = new IdleMediaSync(config, state, _clock, _ => throw new OperationCanceledException());
        Assert.False(idle.Sync(snapshot, () => { }));
        _clock.Sleep(SettledMedia.SettleTime);
        Assert.Throws<OperationCanceledException>(() => idle.Sync(snapshot, () => { }));
        Assert.True(File.Exists(config.MountedCapture.Append("closed.mp4").Display));
        List<IReadOnlyList<string>> retry = [];
        new StorageReplication(config, state, () => { }, args => retry.Add(args.ToArray())).Publish(snapshot);
        Assert.Contains(retry, args => args[0] == "copy");
        Assert.Contains(retry, args => args.Contains("--download"));
    }
}

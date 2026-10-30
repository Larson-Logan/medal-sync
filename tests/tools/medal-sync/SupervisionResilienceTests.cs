using System.Runtime.Versioning;
using MedalSync.Cli;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>Stale handoff requests, a log that cannot be written and notices while Medal runs never disturb a session.</summary>
public sealed class SupervisionResilienceTests : IDisposable
{
    private const string LibraryId = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceIdentity First = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "First PC");
    private static readonly DeviceIdentity Second = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Second PC");

    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private CoordinationStore NewStore()
    {
        string db = _fixture.CreateLibrary("seed");
        return CoordinationStore.Create(_fixture.PathFor("server", LibraryId), LibraryId, LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId),
            new AssetManifest(AssetManifest.CurrentFormat, [], []), First, new string('1', 32), new("2643.583.1", "2643.3040.1"), Now);
    }

    private static HandoffRequest Request(DateTimeOffset at) => new(HandoffRequest.CurrentFormat, Second, Guid.NewGuid().ToString("N"), at);

    private static string RequestFile(CoordinationStore store) => Path.Combine(store.Root, "requests", Second.DeviceId + ".json");

    [Fact]
    public void RequestsExpireByTheRequestersClockOrTheSharesFileTime()
    {
        var store = NewStore();
        store.WriteRequest(Request(Now - TimeSpan.FromHours(1)));
        File.SetLastWriteTimeUtc(RequestFile(store), (Now - TimeSpan.FromHours(1)).UtcDateTime);
        Assert.Single(store.PendingRequests(Now));
        Assert.Empty(store.StaleRequests(Now));

        // Old by the requester's own clock.
        store.WriteRequest(Request(Now - HandoffRequest.Lifetime));
        Assert.Empty(store.PendingRequests(Now));
        Assert.Single(store.StaleRequests(Now));

        // A requester clock running ahead cannot keep it alive once the share says the file is old.
        store.WriteRequest(Request(Now + TimeSpan.FromDays(3)));
        File.SetLastWriteTimeUtc(RequestFile(store), (Now - TimeSpan.FromHours(7)).UtcDateTime);
        Assert.Empty(store.PendingRequests(Now));
        Assert.Single(store.StaleRequests(Now));
    }

    [Fact]
    public void StaleRequestCleanupNeverDeletesANewerRequestFromTheSamePc()
    {
        var store = NewStore();
        var stale = Request(Now - TimeSpan.FromHours(8));
        store.WriteRequest(stale);
        Assert.Equal(stale, Assert.Single(store.StaleRequests(Now)));

        // The PC asks again after the stale request was read but before it is removed.
        var fresh = Request(Now);
        store.WriteRequest(fresh);
        Assert.False(store.RemoveStaleRequest(stale));
        Assert.Equal(fresh, Assert.Single(store.PendingRequests(Now)));

        Assert.True(store.RemoveStaleRequest(store.PendingRequests(Now)[0]));
        Assert.Empty(store.PendingRequests(Now));
        Assert.False(store.RemoveStaleRequest(fresh));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(store.Root, "requests")));
    }

    [Fact]
    public void ALogThatCannotBeWrittenNeverThrowsAndCatchesUpLater()
    {
        string directory = _fixture.PathFor("logs");
        Directory.CreateDirectory(directory);
        string current = Path.Combine(directory, "medal-sync.log");
        Directory.CreateDirectory(current); // Something in the way: every append fails.
        var log = new RotatingLog(directory);

        log.Write("first");
        log.Write("second");
        Assert.Equal(2, log.Held);

        Directory.Delete(current);
        log.Write("third");

        Assert.Equal(0, log.Held);
        var lines = File.ReadAllLines(current);
        Assert.Equal(["first", "second", "third"], lines.Select(l => l[(l.IndexOf(' ') + 1)..]));
    }

    [Fact]
    public void HeldLinesAreBounded()
    {
        string directory = _fixture.PathFor("blocked");
        File.WriteAllText(directory, "a file where the log directory should be");
        var log = new RotatingLog(directory);
        for (int i = 0; i < RotatingLog.MaxHeld + 20; i++) log.Write("line " + i);
        Assert.Equal(RotatingLog.MaxHeld, log.Held);
    }

    [Fact]
    public void WritersInSeveralProcessesShareAndRotateTheLogWithoutLosingLines()
    {
        string directory = _fixture.PathFor("shared-logs");
        // Separate instances stand in for the supervisor and later launcher runs.
        var writers = Enumerable.Range(0, 4).Select(_ => new RotatingLog(directory, maxBytes: 4096, files: 100)).ToArray();
        // Another process holding the current file open for writing must not block appends.
        Directory.CreateDirectory(directory);
        using (new FileStream(Path.Combine(directory, "medal-sync.log"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            Parallel.For(0, writers.Length, w =>
            {
                for (int i = 0; i < 100; i++) writers[w].Write($"writer {w} line {i}");
            });
        }

        Assert.All(writers, w => Assert.Equal(0, w.Held));
        var lines = Directory.EnumerateFiles(directory, "*.log").SelectMany(File.ReadAllLines).ToList();
        Assert.Equal(400, lines.Count);
        Assert.True(Directory.EnumerateFiles(directory, "*.log").Count() > 1, "The log should have rotated.");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void StartupGuardNeverThrowsEvenWhenItsLogFails()
    {
        List<string> log = [];
        SessionCommands.GuardStartup(() => throw new IOException("layout unreadable"), log.Add);
        Assert.Equal("startup: check failed: layout unreadable", Assert.Single(log));

        SessionCommands.GuardStartup(() => throw new UnauthorizedAccessException("registry denied"),
            _ => throw new IOException("disk full"));

        SessionCommands.GuardStartup(() => ["Run value restored"], log.Add);
        Assert.Equal("startup: Run value restored", log[^1]);
    }

    private sealed class Ui : ILauncherUi
    {
        public List<string> Shown { get; } = [];
        public List<string> Notices { get; } = [];
        public void Show(string message) => Shown.Add(message);
        public void Notify(string message) => Notices.Add(message);
        public bool ConfirmLibraryChanges(LibraryChangeReview review) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData("running", false)]
    [InlineData("stopped", true)]
    [InlineData("unknown", false)]
    public void LauncherOnlyTakesFocusOnceMedalHasClosed(string medal, bool dialog)
    {
        var ui = new Ui();
        Launcher.ShowOutcome(ui, "Medal was not started or its session did not finish.", () => medal switch
        {
            "running" => true,
            "stopped" => false,
            _ => throw new InvalidOperationException("process list unavailable"),
        });
        Assert.Equal(dialog ? 1 : 0, ui.Shown.Count);
        Assert.Equal(dialog ? 0 : 1, ui.Notices.Count);
    }
}

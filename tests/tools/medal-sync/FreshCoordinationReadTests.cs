using System.Text;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

/// <summary>
/// A PC kept reading another PC's finished session as in use: Windows served journal.json from a cached open of the
/// replaced file while the directory listing already showed the newer one.
/// </summary>
public sealed class FreshCoordinationReadTests : IDisposable
{
    private static readonly DateTime Old = new(2026, 11, 9, 0, 31, 10, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 11, 9, 0, 32, 21, DateTimeKind.Utc);
    private static readonly DeviceIdentity First = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "First PC");
    private static readonly DeviceIdentity Second = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Second PC");
    private const string LibraryId = "0123456789abcdef0123456789abcdef";

    private readonly MedalFixture _fixture = new();
    private DateTimeOffset _now = new(2026, 11, 9, 0, 40, 0, TimeSpan.Zero);
    private int _opens;
    private readonly List<TimeSpan> _sleeps = [];

    public void Dispose() => _fixture.Dispose();

    private string PauseDirectory => _fixture.PathFor("pauses");

    private FreshCoordinationRead Reader(Func<int, (byte[], DateTime)> open, Func<DateTime?> listing) => new()
    {
        ReadOpen = _ => open(++_opens),
        ReadListing = _ => listing(),
        Now = () => _now,
        Sleep = _sleeps.Add,
        PauseDirectory = PauseDirectory,
    };

    private static (byte[], DateTime) Bytes(string text, DateTime written) => (Encoding.UTF8.GetBytes(text), written);

    private string Path(string name = "journal.json") => _fixture.PathFor("server", name);

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Read_TrustsBytesUnlessTheListingIsNewer(int listingOffsetSeconds)
    {
        // An older listing is only Windows' short directory cache after another PC wrote the file.
        var reader = Reader(_ => Bytes("current", New), () => New.AddSeconds(listingOffsetSeconds));

        Assert.Equal("current", Encoding.UTF8.GetString(reader.Read(Path(), "journal")));
        Assert.Equal(1, _opens);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public void Read_TrustsBytesOfAFileMissingFromTheListing()
    {
        var reader = Reader(_ => Bytes("current", New), () => null);

        Assert.Equal("current", Encoding.UTF8.GetString(reader.Read(Path(), "journal")));
    }

    [Fact]
    public void Read_RetriesAFileReplacedWhileItWasRead()
    {
        var reader = Reader(open => open == 1 ? Bytes("old", Old) : Bytes("new", New), () => New);

        Assert.Equal("new", Encoding.UTF8.GetString(reader.Read(Path(), "journal")));
        Assert.Equal(2, _opens);
        Assert.Equal([FreshCoordinationRead.RetryDelay], _sleeps);
    }

    [Fact]
    public void Read_RefusesPersistentlyStaleBytesAndPausesEveryReaderOfThatFile()
    {
        var stale = Reader(_ => Bytes("old", Old), () => New);

        var refused = Assert.Throws<StaleCoordinationReadException>(() => stale.Read(Path(), "library's ownership journal"));
        Assert.IsAssignableFrom<IOException>(refused);
        Assert.Contains("out-of-date copy of the library's ownership journal", refused.Message);
        Assert.Contains("Nothing was changed", refused.Message);
        Assert.Equal(FreshCoordinationRead.Attempts, _opens);

        // Another process (the tray, a waiting launcher) does not open the file during the pause, so Windows can
        // drop its cached open. A different file is unaffected.
        int before = _opens;
        var other = Reader(_ => Bytes("would be stale", Old), () => New);
        Assert.Throws<StaleCoordinationReadException>(() => other.Read(Path(), "library's ownership journal"));
        Assert.Equal(before, _opens);
        var unrelated = Reader(_ => Bytes("request", New), () => New);
        Assert.Equal("request", Encoding.UTF8.GetString(unrelated.Read(Path("request.json"), "request")));
    }

    [Fact]
    public void Read_FetchesAgainAfterThePauseAndClearsIt()
    {
        bool fresh = false;
        var reader = Reader(_ => fresh ? Bytes("new", New) : Bytes("old", Old), () => New);
        Assert.Throws<StaleCoordinationReadException>(() => reader.Read(Path(), "journal"));
        Assert.Single(Directory.GetFiles(PauseDirectory));

        _now += FreshCoordinationRead.FirstPause;
        fresh = true;

        Assert.Equal("new", Encoding.UTF8.GetString(reader.Read(Path(), "journal")));
        Assert.Empty(Directory.GetFiles(PauseDirectory));
    }

    [Fact]
    public void Read_LengthensThePauseWhileTheCopyStaysStale()
    {
        var reader = Reader(_ => Bytes("old", Old), () => New);
        Assert.Throws<StaleCoordinationReadException>(() => reader.Read(Path(), "journal"));
        foreach (int seconds in new[] { 60, 120, 240, 300, 300 })
        {
            var refusedAt = _now;
            int opens = _opens;
            _now = refusedAt + TimeSpan.FromSeconds(seconds - 1);
            Assert.Throws<StaleCoordinationReadException>(() => reader.Read(Path(), "journal"));
            Assert.Equal(opens, _opens);

            _now = refusedAt + TimeSpan.FromSeconds(seconds);
            Assert.Throws<StaleCoordinationReadException>(() => reader.Read(Path(), "journal"));
            Assert.Equal(opens + FreshCoordinationRead.Attempts, _opens);
        }
    }

    [Fact]
    public void Read_IgnoresAnUnreadablePauseMarker()
    {
        var reader = Reader(_ => Bytes("old", Old), () => New);
        Assert.Throws<StaleCoordinationReadException>(() => reader.Read(Path(), "journal"));
        File.WriteAllText(Assert.Single(Directory.GetFiles(PauseDirectory)), "not a pause");
        int before = _opens;

        Assert.Throws<StaleCoordinationReadException>(() => reader.Read(Path(), "journal"));
        Assert.Equal(before + FreshCoordinationRead.Attempts, _opens);
    }

    [Fact]
    public void Read_IgnoresAPauseThatEndsLaterThanAnyPauseCanLast()
    {
        // The marker was saved while this PC's clock ran three days ahead; the clock has since been corrected.
        _now += TimeSpan.FromDays(3);
        var stale = Reader(_ => Bytes("old", Old), () => New);
        Assert.Throws<StaleCoordinationReadException>(() => stale.Read(Path(), "journal"));
        Assert.Single(Directory.GetFiles(PauseDirectory));
        _now -= TimeSpan.FromDays(3);
        int before = _opens;

        var reader = Reader(_ => Bytes("new", New), () => New);
        Assert.Equal("new", Encoding.UTF8.GetString(reader.Read(Path(), "journal")));
        Assert.Equal(before + 1, _opens);
        Assert.Empty(Directory.GetFiles(PauseDirectory));
    }

    [Fact]
    public void Read_StillObeysAPauseOfTheLongestLength()
    {
        // The longest valid pause, just written: its end is exactly MaxPause ahead and must not be dropped.
        var reader = Reader(_ => Bytes("old", Old), () => New);
        Assert.Throws<StaleCoordinationReadException>(() => reader.Read(Path(), "journal"));
        string marker = Assert.Single(Directory.GetFiles(PauseDirectory));
        File.WriteAllText(marker, $"{(_now + FreshCoordinationRead.MaxPause).UtcTicks} {FreshCoordinationRead.MaxPause.Ticks}");
        int before = _opens;

        Assert.Throws<StaleCoordinationReadException>(() => reader.Read(Path(), "journal"));
        Assert.Equal(before, _opens);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Read_OnARealFileUsesTheListingAndTheSameOpen()
    {
        string path = Path();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "on disk");
        var reader = new FreshCoordinationRead { PauseDirectory = PauseDirectory };

        Assert.Equal("on disk", Encoding.UTF8.GetString(reader.Read(path, "journal")));
        Assert.False(Directory.Exists(PauseDirectory));
    }

    [Fact]
    public void Store_NeverUpdatesOwnershipFromAStaleJournal()
    {
        string root = _fixture.PathFor("library", LibraryId);
        string db = _fixture.CreateLibrary("seed");
        var store = CoordinationStore.Create(root, LibraryId, LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId),
            new AssetManifest(AssetManifest.CurrentFormat, [], []), First, "11111111111111111111111111111111",
            new MedalVersions("2643.583.1", "2643.3040.1"), DateTimeOffset.UnixEpoch);
        var clean = store.ReadJournal();
        string journal = System.IO.Path.Combine(root, "journal.json");
        byte[] cleanBytes = File.ReadAllBytes(journal);

        // The real journal now says another PC is active; this PC's cached open still returns the old Clean bytes.
        store.Update(r => Ownership.Acquire(r, Second, "22222222222222222222222222222222", DateTimeOffset.UnixEpoch), TimeSpan.FromSeconds(5));
        byte[] activeBytes = File.ReadAllBytes(journal);
        store.JournalReader = Reader(_ => (cleanBytes, Old), () => New);

        Assert.Throws<StaleCoordinationReadException>(store.ReadJournal);
        Assert.Throws<StaleCoordinationReadException>(() =>
            store.Update(r => Ownership.Acquire(r, First, "33333333333333333333333333333333", DateTimeOffset.UnixEpoch), TimeSpan.FromSeconds(5)));
        Assert.Equal(activeBytes, File.ReadAllBytes(journal));
        Assert.Equal(OwnershipState.Clean, clean.State);
    }
}

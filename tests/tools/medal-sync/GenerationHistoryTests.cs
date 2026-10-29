using System.Text.Json;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary><c>history list</c> and <c>history diff</c>: read-only views of committed ancestry and changes.</summary>
public sealed class GenerationHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceIdentity First = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "First PC");
    private const string LibraryId = "0123456789abcdef0123456789abcdef";
    private const string Session = "11111111111111111111111111111111";
    private static readonly MedalVersions Medal = new("2643.583.1", "2643.3040.1");
    private static readonly AssetManifest NoAssets = new(AssetManifest.CurrentFormat, [], []);

    private readonly MedalFixture _fixture = new();
    private readonly string _db;
    private readonly GenerationStore _store;

    public void Dispose() => _fixture.Dispose();

    public GenerationHistoryTests()
    {
        _db = _fixture.CreateLibrary("pc");
        _store = new GenerationStore(_fixture.PathFor("generations"), LibraryId, MedalFixture.AccountId);
    }

    private LibrarySnapshot Snapshot() => LibraryDatabase.ReadSnapshot(_db, MedalFixture.AccountId);

    private void Publish(long generation, long? parent, LibrarySnapshot? snapshot = null) =>
        _store.Publish(generation, parent, Session, First, snapshot ?? Snapshot(), NoAssets, Medal, Now.AddHours(generation));

    /// <summary>0 ← 1 ← 3 committed (head 3); 2 is an abandoned attempt on top of 1.</summary>
    private void Branched()
    {
        MedalFixture.AddContent(_db, "keep", video: @"M:\Clips\keep.mp4");
        MedalFixture.AddContent(_db, "edit", video: @"M:\Clips\edit.mp4");
        MedalFixture.AddContent(_db, "deleted", video: @"M:\Clips\deleted.mp4");
        MedalFixture.AddContent(_db, "dropped", video: @"M:\Clips\dropped.mp4");
        Publish(0, null);
        MedalFixture.AddContent(_db, "new", video: @"M:\Clips\Private Title.mp4", metadata: "{\"title\":\"Private Title\",\"tags\":[]}");
        MedalFixture.DeleteContent(_db, "deleted");
        using (var connection = MedalFixture.Open(_db))
        {
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id = 'dropped'");
            MedalFixture.Execute(connection, "UPDATE contents SET video_path = $p, metadata = jsonb($m) WHERE local_content_id = 'edit'",
                ("$p", @"M:\Clips\moved\edit.mp4"), ("$m", "{\"title\":\"Renamed\",\"tags\":[],\"isFavorited\":true}"));
        }
        Publish(1, 0);
        Publish(2, 1);
        Publish(3, 1);
    }

    private string[] Tree() => Directory.EnumerateFileSystemEntries(_store.Directory, "*", SearchOption.AllDirectories)
        .Select(p => p + ":" + (File.Exists(p) ? new FileInfo(p).Length : -1)).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void List_MarksTheHeadAncestryAndUncommittedAttemptsAndPages()
    {
        Branched();
        var before = Tree();
        var list = GenerationHistory.List(_store, 3, GenerationHistory.DefaultLimit, null, Now);
        Assert.Equal(before, Tree());
        Assert.True(list.Complete);
        Assert.Equal(3, list.Head);
        Assert.Equal([3L, 2, 1, 0], list.Entries.Select(e => e.Generation));
        Assert.Equal([true, false, true, true], list.Entries.Select(e => e.Committed));
        Assert.True(list.Entries[0].Head);
        Assert.Equal(1, list.Entries[0].Parent);
        Assert.Equal("First PC", list.Entries[0].Device);
        Assert.Equal("2643.583.1", list.Entries[0].MedalApp);
        Assert.Equal(3, list.Entries[0].RowCounts!["contents"]);
        Assert.Null(list.NextBefore);
        Assert.Contains("uncommitted", GenerationHistory.Render(list), StringComparison.Ordinal);

        var first = GenerationHistory.List(_store, 3, 2, null, Now);
        Assert.Equal([3L, 2], first.Entries.Select(e => e.Generation));
        Assert.Equal(2, first.NextBefore);
        Assert.Contains("--before 2", GenerationHistory.Render(first), StringComparison.Ordinal);
        var second = GenerationHistory.List(_store, 3, 2, first.NextBefore, Now);
        Assert.Equal([1L, 0], second.Entries.Select(e => e.Generation));
        Assert.Null(second.NextBefore);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(list, Json.Options));
        Assert.Equal(HistoryList.CurrentFormat, json.RootElement.GetProperty("format").GetInt32());
    }

    // A head read before another PC publishes describes that head; the newer folder is not yet committed history.
    [Fact]
    public void List_DescribesTheHeadItObserved()
    {
        Branched();
        Publish(4, 3);
        var list = GenerationHistory.List(_store, 3, GenerationHistory.DefaultLimit, null, Now);
        Assert.Equal(3, list.Head);
        Assert.False(list.Entries.Single(e => e.Generation == 4).Committed);
    }

    [Fact]
    public void List_ReportsADamagedOrMissingAncestorAsIncompleteWithoutInventingHistory()
    {
        Branched();
        File.WriteAllText(Path.Combine(_store.Directory, GenerationStore.Name(1), "manifest.json"), "{");
        var damaged = GenerationHistory.List(_store, 3, GenerationHistory.DefaultLimit, null, Now);
        Assert.False(damaged.Complete);
        Assert.Contains("Generation 1", damaged.Problem, StringComparison.Ordinal);
        Assert.Equal("damaged", damaged.Entries.Single(e => e.Generation == 1).Problem);
        Assert.True(damaged.Entries.Single(e => e.Generation == 1).Committed);
        // Below the break nothing is known; an older folder is neither called committed nor abandoned.
        Assert.Null(damaged.Entries.Single(e => e.Generation == 0).Committed);
        Assert.Contains("incomplete", GenerationHistory.Render(damaged), StringComparison.Ordinal);

        Directory.Delete(Path.Combine(_store.Directory, GenerationStore.Name(1)), recursive: true);
        var missing = GenerationHistory.List(_store, 3, GenerationHistory.DefaultLimit, null, Now);
        Assert.False(missing.Complete);
        Assert.Equal("missing", missing.Entries.Single(e => e.Generation == 1).Problem);
        Assert.Null(missing.Entries.Single(e => e.Generation == 0).Committed);
    }

    [Fact]
    public void Diff_CountsAddedRemovedRecordedAndChangedFieldsWithoutTitlesOrPaths()
    {
        Branched();
        var before = Tree();
        var (diff, items) = GenerationHistory.Diff(_store, 3, 0, 1, Now);
        Assert.Equal(before, Tree());
        Assert.True(diff.FromIsAncestor);
        var counts = diff.Counts;
        Assert.Equal(1, counts.Added);
        Assert.Equal(2, counts.Removed);
        Assert.Equal(1, counts.RemovedWithDeletionRecord);
        Assert.Equal(1, counts.RemovedWithoutRecord);
        Assert.Equal(1, counts.Changed);
        Assert.Equal(1, counts.DeletionRecordsAdded);
        Assert.Equal(0, counts.DeletionRecordsRemoved);
        Assert.Equal(1, counts.PathChanges["video_path"]);
        Assert.Equal(1, counts.MetadataFields["title"]);
        Assert.Equal(1, counts.MetadataFields["isFavorited"]);
        Assert.False(counts.MetadataFields.ContainsKey("tags"));

        // Counts only: the default output carries no titles or paths.
        string json = JsonSerializer.Serialize(diff, Json.Options);
        string text = GenerationHistory.Render(diff);
        foreach (string output in new[] { json, text })
        {
            Assert.DoesNotContain("Private Title", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Renamed", output, StringComparison.Ordinal);
            Assert.DoesNotContain(@"M:\", output, StringComparison.Ordinal);
            Assert.DoesNotContain("dropped", output, StringComparison.Ordinal);
        }

        Assert.Equal(["deleted", "dropped", "edit", "new"], items.Select(i => i.ContentId));
        Assert.Equal(GenerationHistory.RemovedWithRecord, items[0].Change);
        Assert.Equal(GenerationHistory.RemovedWithoutRecord, items[1].Change);
        Assert.Equal(@"M:\Clips\dropped.mp4", items[1].Paths["video_path"].Before);
        Assert.Null(items[1].Paths["video_path"].After);
        Assert.Equal(GenerationHistory.Changed, items[2].Change);
        Assert.Equal(["metadata.isFavorited", "metadata.title", "video_path"], items[2].Fields.Order(StringComparer.Ordinal));
        Assert.Equal(@"M:\Clips\moved\edit.mp4", items[2].Paths["video_path"].After);
        Assert.Equal("Private Title", items[3].Title);
    }

    [Fact]
    public void Diff_DetailsGoOnlyToANewFile()
    {
        Branched();
        var (diff, items) = GenerationHistory.Diff(_store, 3, 0, 1, Now);
        string path = _fixture.PathFor("details.json");
        GenerationHistory.WriteDetails(path, diff, items);
        Assert.Contains("Private Title", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.ThrowsAny<IOException>(() => GenerationHistory.WriteDetails(path, diff, items));
    }

    [Fact]
    public void Diff_KnowsSiblingsAndReverseOrderAreNotAncestors()
    {
        Branched();
        Assert.False(GenerationHistory.Diff(_store, 3, 2, 3, Now).Diff.FromIsAncestor);
        Assert.False(GenerationHistory.Diff(_store, 3, 3, 1, Now).Diff.FromIsAncestor);
        var same = GenerationHistory.Diff(_store, 3, 3, 3, Now).Diff;
        Assert.True(same.FromIsAncestor);
        Assert.Equal(0, same.Counts.Added + same.Counts.Removed + same.Counts.Changed);
    }

    [Fact]
    public void Diff_RefusesDamagedMissingAndDifferentSchemaGenerations()
    {
        Branched();
        File.AppendAllText(Path.Combine(_store.Directory, GenerationStore.Name(2), "library.json"), " ");
        var damaged = Assert.Throws<InvalidDataException>(() => GenerationHistory.Diff(_store, 3, 1, 2, Now));
        Assert.Contains("Generation 2 cannot be compared", damaged.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => GenerationHistory.Diff(_store, 3, 1, 9, Now));

        var current = Snapshot();
        var other = LibrarySnapshot.Create(current.AccountId, current.SchemaVersion, new string('e', 64), current.Tables);
        Publish(5, 3, other);
        var schema = Assert.Throws<IncompatibleLibraryException>(() => GenerationHistory.Diff(_store, 3, 3, 5, Now));
        Assert.Contains("different Medal schemas", schema.Message, StringComparison.Ordinal);
    }
}

using MedalSync.Library;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class AssetAndAuditTests : IDisposable
{
    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private sealed class FakeProbe(params string[] existing) : IFileProbe
    {
        public HashSet<string> Reparse { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FileProbeResult Probe(WindowsPath path, WindowsPath root) =>
            new(existing.Contains(path.Display, StringComparer.OrdinalIgnoreCase), false, 10, DateTime.UnixEpoch,
                Reparse.Contains(path.Display));
    }

    private LibrarySnapshot Library()
    {
        string db = _fixture.CreateLibrary("pc");
        MedalFixture.AddContent(db, "a", video: @"M:\Clips\Clips\a.mp4", thumbnail: @"M:\Clips\Thumbnails\a.jpg",
            metadata: "{\"keyboardOverlayFilePath\":\"M:\\\\Clips\\\\Temp\\\\InputActions\\\\a.json\","
                + "\"remoteContent\":{\"localContentUrl\":\"C:\\\\Medal\\\\Clips\\\\old.mp4\"},"
                + "\"plugin\":{\"cache\":\"C:\\\\Users\\\\someone\\\\cache.bin\"}}");
        MedalFixture.AddContent(db, "b", video: @"M:\Clips\..\Windows\b.mp4");
        MedalFixture.AddContent(db, "c", video: @"D:\Elsewhere\c.mp4");
        return LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
    }

    [Fact]
    public void Collect_FindsColumnsAndKnownMetadataButSkipsTheCloudRecord()
    {
        var references = AssetInventory.Collect(Library());

        Assert.Contains(references, r => r is { ContentId: "a", Field: "video_path", Role: AssetRole.Required });
        Assert.Contains(references, r => r is { ContentId: "a", Field: "thumbnail_path", Role: AssetRole.Required });
        Assert.Contains(references, r => r is { Field: "metadata:$.keyboardOverlayFilePath", Role: AssetRole.Optional });
        Assert.Contains(references, r => r is { Field: "metadata:$.plugin.cache", Role: AssetRole.Unrecognized });
        Assert.DoesNotContain(references, r => r.Path.Contains("old.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_ClassifiesEveryReference()
    {
        var probe = new FakeProbe(@"M:\Clips\Clips\a.mp4");
        probe.Reparse.Add(@"M:\Clips\Thumbnails\a.jpg");
        var report = AssetInventory.Check(AssetInventory.Collect(Library()), [WindowsPath.Parse(@"M:\Clips")], probe);

        AssetProblem For(string id, string field) => report.Items.Single(i => i.Reference.ContentId == id && i.Reference.Field == field).Problem;
        Assert.Equal(AssetProblem.None, For("a", "video_path"));
        Assert.Equal(AssetProblem.ReparsePoint, For("a", "thumbnail_path"));
        Assert.Equal(AssetProblem.Missing, For("a", "metadata:$.keyboardOverlayFilePath"));
        Assert.Equal(AssetProblem.InvalidPath, For("b", "video_path"));
        Assert.Equal(AssetProblem.OutsideAllowedRoots, For("c", "video_path"));
        Assert.Equal(AssetProblem.OutsideAllowedRoots, For("a", "metadata:$.plugin.cache"));
        // A missing optional overlay and an unrecognized metadata path are warnings;
        // the reparse point, the traversal and the out-of-root video block.
        Assert.Equal(3, report.Blocking.Count());
        Assert.DoesNotContain(report.Blocking, i => i.Reference.Role != AssetRole.Required && i.Problem is AssetProblem.Missing or AssetProblem.OutsideAllowedRoots);
    }

    [Fact]
    public void FileSystemProbe_ReportsRealFiles()
    {
        string root = _fixture.PathFor("capture");
        Directory.CreateDirectory(Path.Combine(root, "Clips"));
        File.WriteAllBytes(Path.Combine(root, "Clips", "a.mp4"), new byte[123]);
        var probe = new FileSystemProbe();
        var rootPath = WindowsPath.Parse(root);

        var present = probe.Probe(WindowsPath.Parse(Path.Combine(root, "Clips", "a.mp4")), rootPath);
        var absent = probe.Probe(WindowsPath.Parse(Path.Combine(root, "Clips", "missing.mp4")), rootPath);
        var folder = probe.Probe(WindowsPath.Parse(Path.Combine(root, "Clips")), rootPath);

        Assert.True(present.Exists);
        Assert.Equal(123, present.Length);
        Assert.False(present.HasReparsePoint);
        Assert.False(absent.Exists);
        Assert.True(folder.IsDirectory);
    }

    [Fact]
    public void Audit_DetectsMissingMediaChangesAndRepairsOnlyWhatExists()
    {
        string db = _fixture.CreateLibrary("pc");
        MedalFixture.AddContent(db, "kept", video: @"M:\Clips\Clips\kept.mp4", metadata: "{\"title\":\"k\",\"contentInode\":\"7\",\"contentSize\":5}");
        MedalFixture.AddContent(db, "cleared", video: @"M:\Clips\Clips\cleared.mp4", thumbnail: @"M:\Clips\Thumbnails\cleared.jpg",
            metadata: "{\"title\":\"c\",\"contentInode\":\"8\",\"contentSize\":6}");
        MedalFixture.AddContent(db, "reaped", video: @"M:\Clips\Clips\reaped.mp4");
        MedalFixture.AddContent(db, "gone", video: @"M:\Clips\Clips\gone.mp4");
        MedalFixture.AddContent(db, "deleted", video: @"M:\Clips\Clips\deleted.mp4");
        var checkpoint = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        // What Medal does when files vanish behind a reachable drive root.
        using (var connection = MedalFixture.Open(db))
        {
            MedalFixture.Execute(connection, "UPDATE contents SET video_path = NULL, thumbnail_path = NULL, metadata = jsonb_remove(metadata, '$.contentInode', '$.contentSize') WHERE local_content_id = 'cleared'");
            MedalFixture.Execute(connection, "DELETE FROM contents WHERE local_content_id IN ('reaped', 'gone')");
        }
        MedalFixture.DeleteContent(db, "deleted");
        var after = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);

        var audit = MissingMediaAudit.Compare(checkpoint, after);
        Assert.Equal(2, audit.Changes.Count(c => c.Kind == MediaChangeKind.PathCleared));
        Assert.Equal(2, audit.Changes.Count(c => c.Kind == MediaChangeKind.RemovedWithoutRecord));
        Assert.Single(audit.Changes, c => c.Kind == MediaChangeKind.RemovedWithRecord);

        string[] onDisk = [@"M:\Clips\Clips\cleared.mp4", @"M:\Clips\Thumbnails\cleared.jpg", @"M:\Clips\Clips\reaped.mp4"];
        var repaired = MissingMediaAudit.ProposeRepair(checkpoint, after, p => onDisk.Contains(p), out var applied);

        Assert.Equal(3, applied.Count);
        var contents = repaired.Table("contents");
        var cleared = contents.Rows.Single(r => TableSnapshot.Text(r[contents.ColumnIndex("local_content_id")]) == "cleared");
        Assert.Equal(@"M:\Clips\Clips\cleared.mp4", TableSnapshot.Text(cleared[contents.ColumnIndex("video_path")]));
        Assert.Contains("\"contentInode\":\"8\"", ((JsonbCell)cleared[contents.ColumnIndex("metadata")]!).Json, StringComparison.Ordinal);
        var ids = contents.Rows.Select(r => TableSnapshot.Text(r[contents.ColumnIndex("local_content_id")])).ToHashSet();
        Assert.Contains("reaped", ids);
        Assert.DoesNotContain("gone", ids);
        Assert.DoesNotContain("deleted", ids);

        // The repaired snapshot applies cleanly, with counters rebuilt by Medal's triggers.
        var withCounts = CountRebuilder.Rebuild(repaired, db);
        var result = LibraryRestorer.Restore(db, withCounts, new RestoreOptions(MedalFixture.AccountId, _fixture.PathFor("backups")), DateTimeOffset.UnixEpoch);
        Assert.True(result.Changed);
        Assert.Equal(withCounts.Digest(), LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId).Digest());
    }
}

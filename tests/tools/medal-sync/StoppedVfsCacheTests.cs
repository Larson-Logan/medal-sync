using MedalSync.Platform;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

/// <summary>
/// The shutdown finish restarts nothing when the stopped drive's cache holds only clean read copies.
/// The layout and metadata mirror a real session cache: the library marker that every health check reads
/// stays cached with <c>"Dirty": false</c> for the whole session.
/// </summary>
public sealed class StoppedVfsCacheTests : IDisposable
{
    private const string Clean = """{ "ModTime": "2026-10-26T14:19:08-05:00", "Size": 33, "Rs": [ { "Pos": 0, "Size": 33 } ], "Dirty": false }""";
    private const string Dirty = """{ "ModTime": "2026-11-21T17:37:00-06:00", "Size": 4096, "Rs": [ { "Pos": 0, "Size": 4096 } ], "Dirty": true }""";
    private readonly MedalFixture _fixture = new();
    private readonly string _cache;

    public StoppedVfsCacheTests() => _cache = _fixture.PathFor("cache", "session");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void MissingOrEmptyCacheHoldsNoUploads()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
        Assert.True(StoppedVfsCache.HoldsNoUnsentFiles(_cache));
        Directory.CreateDirectory(Path.Combine(_cache, "vfsMeta", "local", "Clips"));
        Assert.True(StoppedVfsCache.HoldsNoUnsentFiles(_cache));
    }

    [Fact]
    public void CleanReadCopiesHoldNoUploads()
    {
        Cached(@"Clips\.medal-sync-library", Clean, data: new string('a', 32) + "\n");
        Cached(@"Clips\Databases\medal.db", Clean.Replace("33", "6737920"), data: "synthetic database copy");
        // Created and never written: rclone keeps the metadata but no data file.
        Cached(@"Clips\4muria1z.4ln", Clean.Replace("33", "0"), data: null);

        Assert.True(StoppedVfsCache.HoldsNoUnsentFiles(_cache));
    }

    [Fact]
    public void DirtyEntryIsUnsent()
    {
        Cached(@"Clips\.medal-sync-library", Clean, data: new string('a', 32) + "\n");
        Cached(@"Clips\Game\clip.mp4", Dirty, data: "synthetic unsent clip");

        Assert.False(StoppedVfsCache.HoldsNoUnsentFiles(_cache));
    }

    [Theory]
    [InlineData("{ \"Size\": 33 }")]
    [InlineData("{ \"Dirty\": \"false\" }")]
    [InlineData("{ \"Dirty\": null }")]
    [InlineData("[ false ]")]
    [InlineData("not json")]
    [InlineData("")]
    public void UnprovenMetadataIsUnsent(string metadata)
    {
        Cached(@"Clips\Game\clip.mp4", metadata, data: "synthetic clip");

        Assert.False(StoppedVfsCache.HoldsNoUnsentFiles(_cache));
    }

    [Fact]
    public void DataWithoutMetadataIsUnsent()
    {
        Cached(@"Clips\.medal-sync-library", Clean, data: "marker");
        string orphan = Path.Combine(_cache, "vfs", "local", "Clips", "Game", "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllText(orphan, "synthetic clip without metadata");

        Assert.False(StoppedVfsCache.HoldsNoUnsentFiles(_cache));
    }

    [Fact]
    public void UnknownCacheLayoutIsUnsent()
    {
        Cached(@"Clips\.medal-sync-library", Clean, data: "marker");
        File.WriteAllText(Path.Combine(_cache, "stray.bin"), "synthetic");

        Assert.False(StoppedVfsCache.HoldsNoUnsentFiles(_cache));
    }

    private void Cached(string relative, string metadata, string? data)
    {
        string meta = Path.Combine(_cache, "vfsMeta", "local", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(meta, metadata);
        if (data is null) return;
        string file = Path.Combine(_cache, "vfs", "local", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, data);
    }
}

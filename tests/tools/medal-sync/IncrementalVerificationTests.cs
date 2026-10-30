using MedalSync.Config;
using MedalSync.Storage;
using MedalSync.Util;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

public sealed class IncrementalVerificationTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private MedalSyncConfig Config => new(1, new string('a', 32), MedalFixture.AccountId, @"C:\Journal", @"C:\Media",
        @"M:\Clips", @"E:\Cache", "rclone.exe", new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, [])
        { StorageMode = StorageMode.Local, Replication = new(null, null, []) { FullVerifyEveryHandoffs = 3 } };
    private string Source => _fixture.PathFor("media");
    private LocalState State => new(_fixture.PathFor("state"));
    private void Media(string name, string bytes = "synthetic")
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, name), bytes);
    }
    private (string[] Bytes, string[] Sizes, bool Full) Pass(MedalSyncConfig? config = null, string target = "cloud:folder",
        DateTimeOffset? now = null, Action? end = null, bool fail = false, string[]? files = null)
    {
        string[] bytes = [], sizes = [];
        bool full = false;
        new ReplicationVerification(config ?? Config, State, Source, target, new string('b', 64), files ?? ["one.mp4"], now ?? _now)
            .Run((delta, _) => { if (fail) throw new IOException("interrupted"); bytes = delta.ToArray(); },
                same => sizes = same.ToArray(), (_, _, f) => full = f, end ?? (() => { }));
        return (bytes, sizes, full);
    }

    [Fact]
    public void UnchangedLibraryUsesListingAndEditedMediaIsByteVerified()
    {
        Media("one.mp4"); Media("two.mp4");
        Assert.Equal(2, Pass(files: ["one.mp4", "two.mp4"]).Bytes.Length);
        var same = Pass(files: ["one.mp4", "two.mp4"]);
        Assert.Empty(same.Bytes);
        Assert.Equal(2, same.Sizes.Length);
        Media("one.mp4", "edited synthetic media");
        var changed = Pass(files: ["one.mp4", "two.mp4"]);
        Assert.Equal(new[] { "one.mp4" }, changed.Bytes);
        Assert.Equal(new[] { "two.mp4" }, changed.Sizes);
    }

    [Fact]
    public void FullPassRunsAtHandoffAndAgeBounds()
    {
        Media("one.mp4");
        Assert.True(Pass().Full);
        Assert.False(Pass().Full);
        Assert.False(Pass().Full);
        Assert.True(Pass().Full);
        Assert.True(Pass(now: _now.AddDays(7)).Full);
    }

    [Fact]
    public void MissingCorruptForeignAndChangedConfigurationReceiptsNeverSkipBytes()
    {
        Media("one.mp4"); Pass();
        var differentLibrary = Config with { LibraryId = new string('c', 32) };
        Assert.True(Pass(differentLibrary).Full);
        Assert.True(Pass(target: "other:folder").Full);
        foreach (string file in Directory.GetFiles(Path.Combine(State.Root, "replication", "destinations"))) File.WriteAllText(file, "{broken");
        Assert.True(Pass().Full);
        Assert.True(Pass(Config with { Replication = Config.Replication! with { BandwidthLimitMiB = 2 } }).Full);
    }

    [Fact]
    public void InterruptedPassAndSourceMutationInvalidateTheReceipt()
    {
        Media("one.mp4"); Pass();
        Assert.Throws<IOException>(() => Pass(fail: true));
        Assert.True(Pass().Full);
        Assert.Throws<IOException>(() => Pass(end: () => Media("one.mp4", "changed while checking")));
        Assert.True(Pass().Full);
    }

    [Fact]
    public void StructurallyValidButAlteredReceiptForcesAFullPass()
    {
        Media("one.mp4"); Pass();
        string path = Assert.Single(Directory.GetFiles(Path.Combine(State.Root, "replication", "destinations")));
        var receipt = Json.ReadFile<DestinationVerification>(path, "synthetic receipt");
        AtomicFile.Replace(path, Json.Serialize(receipt with { SnapshotDigest = new string('c', 64) }));
        Assert.True(Pass().Full);
    }

    [Fact]
    public void DefaultAlwaysChecksBytesAndBoundsAreValidated()
    {
        Media("one.mp4");
        var config = Config with { Replication = new(null, null, []) };
        Assert.True(Pass(config).Full);
        Assert.True(Pass(config).Full);
        foreach (int bad in new[] { 0, 101 })
            Assert.Throws<InvalidDataException>((Config with { Replication = Config.Replication! with { FullVerifyEveryHandoffs = bad } }).Validate);
        foreach (int bad in new[] { 0, 31 })
            Assert.Throws<InvalidDataException>((Config with { Replication = Config.Replication! with { FullVerifyEveryDays = bad } }).Validate);
    }

    public void Dispose() => _fixture.Dispose();
}

using LarsonServer.MedalSync.Config;
using LarsonServer.MedalSync.Storage;
using LarsonServer.MedalSync.Tests.Fixtures;
using LarsonServer.MedalSync.Util;

namespace LarsonServer.MedalSync.Tests;

public sealed class StoragePromotionTests : IDisposable
{
    private const string SpikeId = "11111111111111111111111111111111";
    private const string LibraryId = "22222222222222222222222222222222";
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();
    private string Source => _fixture.PathFor("spike", "Clips");
    private string Target => _fixture.PathFor("production", "Clips");
    private string Receipt => _fixture.PathFor("private", "move");

    private void Prepare()
    {
        Directory.CreateDirectory(Path.Combine(Source, "nested"));
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Source, MedalSyncConfig.StorageMarker), SpikeId + "\n");
        File.WriteAllText(Path.Combine(Source, "nested", "clip.mp4"), "synthetic media");
    }

    [Fact]
    public void PreviewChangesNothingAndApplyPreservesHashesDestinationMarkerAndCheckpoint()
    {
        Prepare();
        var paths = StoragePromotion.Inspect(Source, Target, Receipt, SpikeId, LibraryId);
        Assert.Equal(2, paths.Count);
        Assert.False(Directory.Exists(Receipt));
        var result = StoragePromotion.Apply(Source, Target, Receipt, SpikeId, LibraryId, p => File.WriteAllText(p, "private checkpoint"));
        Assert.Equal("complete", result.Stage);
        Assert.False(Directory.Exists(Source));
        Assert.True(Directory.Exists(result.PreservedTarget));
        Assert.Equal(SpikeId + "\n", File.ReadAllText(result.PreservedMarker));
        Assert.False(File.Exists(Path.Combine(Target, MedalSyncConfig.StorageMarker)));
        Assert.Equal("synthetic media", File.ReadAllText(Path.Combine(Target, "nested", "clip.mp4")));
        Assert.Equal(Hashing.Sha256File(Path.Combine(Target, "nested", "clip.mp4")), result.Files.Single(f => f.Relative.EndsWith("clip.mp4", StringComparison.Ordinal)).Sha256);
        Assert.Equal("private checkpoint", File.ReadAllText(Path.Combine(Receipt, "before-promotion.json")));
        // A repeated command is a no-op even after production adds files.
        File.WriteAllText(Path.Combine(Target, MedalSyncConfig.StorageMarker), LibraryId);
        File.WriteAllText(Path.Combine(Target, "new.mp4"), "new recording");
        Assert.Empty(StoragePromotion.Inspect(Source, Target, Receipt, SpikeId, LibraryId));
        var repeated = StoragePromotion.Apply(Source, Target, Receipt, SpikeId, LibraryId, _ => throw new Exception("Must not run"));
        Assert.Equal(result.Files, repeated.Files);
        Assert.True(File.Exists(Path.Combine(Target, "new.mp4")));
    }

    [Fact]
    public void RefusesNonemptyTargetWrongMarkerNestedPathsAndUnrelatedReceipts()
    {
        Prepare();
        File.WriteAllText(Path.Combine(Target, "keep.txt"), "keep");
        Assert.Throws<UnsafeOperationException>(() => StoragePromotion.Inspect(Source, Target, Receipt, SpikeId, LibraryId));
        File.Delete(Path.Combine(Target, "keep.txt"));
        Assert.Throws<UnsafeOperationException>(() => StoragePromotion.Inspect(Source, Target, Receipt, LibraryId, SpikeId));
        Assert.Throws<UnsafeOperationException>(() => StoragePromotion.Inspect(Source, Path.Combine(Source, "nested"), Receipt, SpikeId, LibraryId));
        Assert.Throws<UnsafeOperationException>(() => StoragePromotion.Inspect(Source, Target, Path.Combine(Source, "private"), SpikeId, LibraryId));
        Directory.CreateDirectory(Receipt);
        File.WriteAllText(Path.Combine(Receipt, "unrelated.txt"), "keep");
        Assert.Throws<UnsafeOperationException>(() => StoragePromotion.Inspect(Source, Target, Receipt, SpikeId, LibraryId));
        Assert.True(Directory.Exists(Source));
    }

    [Fact]
    public void PrivateReceiptCannotContainEitherMediaTree()
    {
        Prepare();
        Assert.Throws<UnsafeOperationException>(() => StoragePromotion.Inspect(Source, Target, Path.GetDirectoryName(Source)!, SpikeId, LibraryId));
        Assert.Throws<UnsafeOperationException>(() => StoragePromotion.Inspect(Source, Target, Path.GetDirectoryName(Target)!, SpikeId, LibraryId));
        Assert.True(File.Exists(Path.Combine(Source, "nested", "clip.mp4")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
    }

    [Fact]
    public void ACheckpointFailureNeverMovesAnything()
    {
        Prepare();
        Assert.Throws<IOException>(() => StoragePromotion.Apply(Source, Target, Receipt, SpikeId, LibraryId, _ => throw new IOException("Medal reopened")));
        Assert.True(Directory.Exists(Source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
    }

    [Fact]
    public void ChangedMediaStopsVerificationAndAnInterruptedMoveCannotBeRepeatedBlindly()
    {
        Prepare();
        Assert.Throws<IOException>(() => StoragePromotion.Apply(Source, Target, Receipt, SpikeId, LibraryId, p =>
        {
            File.WriteAllText(p, "private checkpoint");
            File.WriteAllText(Path.Combine(Source, "nested", "clip.mp4"), "changed during hashing");
        }));
        Assert.False(Directory.Exists(Source));
        Assert.Equal("changed during hashing", File.ReadAllText(Path.Combine(Target, "nested", "clip.mp4")));
        Assert.Equal("moved", Json.ReadFile<PromotionReceipt>(Path.Combine(Receipt, "receipt.json"), "receipt").Stage);
        Assert.Throws<UnsafeOperationException>(() => StoragePromotion.Inspect(Source, Target, Receipt, SpikeId, LibraryId));
        Assert.True(File.Exists(Path.Combine(Target, MedalSyncConfig.StorageMarker)));
    }
}

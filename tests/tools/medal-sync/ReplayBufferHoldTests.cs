using MedalSync.Config;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>Holding Medal's replay-buffer segments on this PC while recording, with every other upload released (#201).</summary>
public sealed class ReplayBufferHoldTests
{
    private const string Prefix = "Clips/Temp/RecordingBuffer/";
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan WriteBack = TimeSpan.FromSeconds(5);

    private static UploadQueueItem Item(ulong id, string? name, double? expiry, bool uploading = false) => new(id, 0, uploading, name, expiry);

    private static IReadOnlyList<ulong> Due(bool includeBuffer, params UploadQueueItem[] queue) =>
        UploadRelease.Due(queue, Prefix, includeBuffer, Hold, WriteBack);

    [Fact]
    public void BufferPrefixFollowsTheCaptureFolderOnTheDrive()
    {
        Assert.Equal(Prefix, UploadRelease.BufferPrefix(WindowsPath.Parse(@"M:\Clips")));
        Assert.Equal("Videos/Medal/Temp/RecordingBuffer/", UploadRelease.BufferPrefix(WindowsPath.Parse(@"M:\Videos\Medal")));
    }

    [Fact]
    public void WhileRecordingSavedClipsAreReleasedAfterTheNormalDelayAndSegmentsStay()
    {
        var due = Due(false,
            Item(1, "Clips/Clips/new.mp4", 590),                     // Closed 10 s ago: release.
            Item(2, "Clips/Clips/just-closed.mp4", 598),             // Closed 2 s ago: still within the normal delay.
            Item(3, Prefix + "segment-0001.mp4", 590),               // Replay buffer: held.
            Item(4, "clips/temp/recordingbuffer/segment-0002.MP4", 100), // Case does not matter on Windows.
            Item(5, "Clips/Temp/overlay.json", 590));                // Medal's other Temp files upload as before.
        Assert.Equal([1UL, 5UL], due);
    }

    [Fact]
    public void TheBufferFolderBoundaryIsExact()
    {
        Assert.Equal([1UL, 2UL], Due(false,
            Item(1, "Clips/Temp/RecordingBufferOld/segment.mp4", 590),
            Item(2, "Clips/Temp/RecordingBuffer", 590)));
        Assert.True(UploadRelease.InBuffer("/" + Prefix + "nested/segment.mp4", Prefix));
        Assert.True(UploadRelease.InBuffer(@"Clips\Temp\RecordingBuffer\segment.mp4", Prefix));
    }

    [Fact]
    public void RunningEligibleAndUnclassifiableItemsAreHandledConservatively()
    {
        Assert.Equal([3UL, 4UL], Due(false,
            Item(1, "Clips/Clips/a.mp4", 590, uploading: true), // Cannot be changed while it uploads.
            Item(2, "Clips/Clips/b.mp4", 0),                   // Already eligible.
            Item(3, null, 590),                                // No name: released, as without the hold.
            Item(4, Prefix + "segment.mp4", null)));           // No expiry: released.
    }

    [Fact]
    public void NotRecordingOrFinishingReleasesEverything()
    {
        Assert.Equal([1UL, 2UL], Due(true, Item(1, Prefix + "segment.mp4", 590), Item(2, "Clips/Clips/new.mp4", 599)));
        Assert.Empty(Due(true, Item(3, Prefix + "segment.mp4", -1)));
    }

    [Fact]
    public void QueueNamesAndExpiryAreParsedAndMalformedOnesAreLeftOut()
    {
        const string stats = "{\"fs\":\"//192.0.2.10/Data/Media/Videos\",\"diskCache\":{\"uploadsInProgress\":0,\"uploadsQueued\":3,\"erroredFiles\":0,\"outOfSpace\":false}}";
        var verdict = VfsStatus.Evaluate(stats, "{\"queue\":["
            + "{\"name\":\"Clips/Temp/RecordingBuffer/a.mp4\",\"id\":1,\"size\":1,\"expiry\":598.5,\"tries\":0,\"uploading\":false},"
            + "{\"name\":7,\"id\":2,\"size\":1,\"expiry\":\"soon\",\"tries\":0,\"uploading\":false},"
            + "{\"id\":3,\"size\":1,\"tries\":0,\"uploading\":false}]}", @"\\192.0.2.10\Data\Media\Videos");
        Assert.Equal(DrainState.Pending, verdict.State);
        Assert.Equal(new UploadQueueItem(1, 0, false, "Clips/Temp/RecordingBuffer/a.mp4", 598.5), verdict.Uploads[0]);
        Assert.Equal(new UploadQueueItem(2, 0, false), verdict.Uploads[1]);
        Assert.Equal(new UploadQueueItem(3, 0, false), verdict.Uploads[2]);
    }

    private static MedalSyncConfig Server(int? hold, string? localCopy = null) =>
        new(1, new string('c', 32), "12345", @"\\192.0.2.10\Data\Coordination", @"\\192.0.2.10\Data\Media", @"M:\Clips", @"E:\Cache",
            @"C:\Tools\rclone.exe", new(5, 20, 20, 300, 30, 1000, 2, 53684) { RecordingBufferHoldSeconds = hold }, 4, [])
        {
            Replication = localCopy is null ? null : new(localCopy, null, []),
        };

    [Fact]
    public void TheHoldAppliesOnlyToARecordingDriveServingPrimaryStorageDirectly()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), Server(600).BufferHold(forRecording: true));
        Assert.Null(Server(600).BufferHold(forRecording: false));
        Assert.Null(Server(null).BufferHold(forRecording: true));
        Assert.Null(Server(600, @"D:\MedalLocal").BufferHold(forRecording: true));
        Assert.Equal(TimeSpan.FromSeconds(600), Server(600).MountSettings(new string('0', 32), @"E:\Cache\rclone.log").WriteBack);
        Assert.Equal(TimeSpan.FromSeconds(5), Server(600).MountSettings(new string('0', 32), @"E:\Cache\rclone.log", forRecording: false).WriteBack);
        Assert.Equal(TimeSpan.FromSeconds(5), Server(null).MountSettings(new string('0', 32), @"E:\Cache\rclone.log").WriteBack);
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void TheHoldIsBounded(int seconds, bool valid)
    {
        var config = Server(seconds);
        if (valid) config.Validate();
        else Assert.Contains("recordingBufferHoldSeconds", Assert.Throws<InvalidDataException>(config.Validate).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNormalWriteBackDelayIsValidatedEvenWhenTheHoldReplacesIt()
    {
        var server = Server(600);
        var config = server with { Mount = server.Mount with { WriteBackSeconds = -1 } };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void TheSettingIsLeftOutOfTheFileUntilSet()
    {
        Assert.DoesNotContain("recordingBufferHoldSeconds", System.Text.Encoding.UTF8.GetString(Json.Serialize(Server(null))), StringComparison.Ordinal);
        Assert.Contains("\"recordingBufferHoldSeconds\": 600", System.Text.Encoding.UTF8.GetString(Json.Serialize(Server(600))), StringComparison.Ordinal);
    }
}

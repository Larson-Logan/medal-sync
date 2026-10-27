using System.Text.Json.Nodes;
using LarsonServer.MedalSync.Cli;
using LarsonServer.MedalSync.Config;
using LarsonServer.MedalSync.Coordination;
using LarsonServer.MedalSync.Library;
using LarsonServer.MedalSync.Platform;
using LarsonServer.MedalSync.Tests.Fixtures;
using LarsonServer.MedalSync.Util;

namespace LarsonServer.MedalSync.Tests;

public sealed class ReconciliationTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private readonly string _shared;
    private readonly string _incoming;
    private static readonly WindowsPath Source = WindowsPath.Parse(@"D:\Capture");
    private static readonly WindowsPath Destination = WindowsPath.Parse(@"M:\Clips\Devices\second");

    public ReconciliationTests()
    {
        _shared = _fixture.CreateLibrary("shared");
        _incoming = _fixture.CreateLibrary("incoming");
    }
    public void Dispose() => _fixture.Dispose();
    private LibrarySnapshot Read(string db) => LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
    private ReconciliationResult Plan() => LibraryReconciliation.Plan(Read(_shared), Read(_incoming), Source, Destination);
    private static IReadOnlyList<Cell?> Row(LibrarySnapshot snapshot, string id) => snapshot.Table("contents").Rows.Single(
        r => TableSnapshot.Text(r[snapshot.Table("contents").ColumnIndex("local_content_id")]) == id);
    private static string? Field(LibrarySnapshot snapshot, string id, string column) => TableSnapshot.Text(Row(snapshot, id)[snapshot.Table("contents").ColumnIndex(column)]);
    private static JsonObject Metadata(LibrarySnapshot snapshot, string id) => (JsonNode.Parse(((JsonbCell)Row(snapshot, id)[snapshot.Table("contents").ColumnIndex("metadata")]!).Json) as JsonObject)!;

    [Fact]
    public void CloudIdentityPreservesSharedIdAndAddsIncomingRecordingAndBookmarks()
    {
        MedalFixture.AddContent(_shared, "server-id", remoteId: "uploaded", metadata: "{\"title\":\"Ace\",\"remoteContent\":{\"views\":2}}");
        MedalFixture.AddContent(_incoming, "local-id", video: @"D:\Capture\clip.mp4", remoteId: "uploaded",
            metadata: "{\"title\":\"Ace\",\"bookmarks\":[{\"time\":12}],\"codecName\":\"h264\",\"remoteContent\":{\"views\":1}}");
        var plan = Plan();
        Assert.Empty(plan.Conflicts);
        Assert.Equal(1, plan.Matched);
        Assert.Equal(0, plan.Added);
        Assert.Equal(1, plan.Snapshot.RowCount("contents"));
        Assert.Equal(@"M:\Clips\Devices\second\clip.mp4", Field(plan.Snapshot, "server-id", "video_path"));
        Assert.Equal(12, Metadata(plan.Snapshot, "server-id")["bookmarks"]![0]!["time"]!.GetValue<int>());
        Assert.Equal(2, Metadata(plan.Snapshot, "server-id")["remoteContent"]!["views"]!.GetValue<int>());
        Assert.Equal(new IdentityMapping("local-id", "server-id"), Assert.Single(plan.Identities));
        Assert.Equal(@"D:\Capture\clip.mp4", Assert.Single(plan.Files).Source);
    }

    [Fact]
    public void UnionRetainsUniqueCloudOnlyAndLocalEntriesAndNeverInfersDeletionFromAbsence()
    {
        MedalFixture.AddContent(_shared, "only-server", video: @"M:\Clips\old.mp4");
        MedalFixture.AddContent(_incoming, "only-second", video: @"D:\Capture\old.mp4");
        MedalFixture.AddContent(_incoming, "cloud", remoteId: "cloud");
        var plan = Plan();
        Assert.Empty(plan.Conflicts);
        Assert.Equal(3, plan.Snapshot.RowCount("contents"));
        Assert.Equal(@"M:\Clips\old.mp4", Field(plan.Snapshot, "only-server", "video_path"));
        Assert.Equal(@"M:\Clips\Devices\second\old.mp4", Field(plan.Snapshot, "only-second", "video_path"));
        Assert.Null(Field(plan.Snapshot, "cloud", "video_path"));
    }

    [Theory]
    [InlineData("title", "\"First title\"", "\"Second title\"")]
    [InlineData("isFavorited", "false", "true")]
    [InlineData("bookmarks", "[{\"time\":1}]", "[{\"time\":2}]")]
    [InlineData("unknownFutureSetting", "1", "2")]
    public void CompetingUserMetadataRequiresReview(string field, string a, string b)
    {
        MedalFixture.AddContent(_shared, "server", remoteId: "same", metadata: "{\"" + field + "\":" + a + "}");
        MedalFixture.AddContent(_incoming, "second", remoteId: "same", metadata: "{\"" + field + "\":" + b + "}");
        Assert.Contains(Plan().Conflicts, c => c.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void CachedAvatarAndEmptyTitleDifferencesDoNotCreateUserMetadataConflicts()
    {
        MedalFixture.AddContent(_shared, "server", remoteId: "same", metadata: "{\"title\":\"\",\"userTags\":[{\"userId\":\"1\",\"userName\":\"friend\",\"thumbnail\":\"new\"}]}");
        MedalFixture.AddContent(_incoming, "second", remoteId: "same", metadata: "{\"title\":null,\"userTags\":[{\"userId\":\"1\",\"userName\":\"friend\",\"thumbnail\":\"old\"}]}");
        Assert.Empty(Plan().Conflicts);
    }

    [Fact]
    public void DistinctUserTagIdentitiesCannotBeCollapsedAsAnAvatarChange()
    {
        MedalFixture.AddContent(_shared, "server", remoteId: "same", metadata: "{\"userTags\":[{\"userId\":\"1\"}]}");
        MedalFixture.AddContent(_incoming, "second", remoteId: "same", metadata: "{\"userTags\":[{\"userId\":\"2\"}]}");
        Assert.Contains(Plan().Conflicts, c => c.Contains("userTags", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentVideosOfTheSameIdentityAreNeverSilentlyCollapsed()
    {
        MedalFixture.AddContent(_shared, "server", video: @"M:\Clips\original.mp4", remoteId: "same");
        MedalFixture.AddContent(_incoming, "second", video: @"D:\Capture\edit.mp4", remoteId: "same");
        Assert.Contains(Plan().Conflicts, c => c.Contains("video_path", StringComparison.Ordinal));
    }

    [Fact]
    public void ParentLinksFollowCanonicalSharedIdentity()
    {
        MedalFixture.AddContent(_shared, "server-parent", remoteId: "same");
        MedalFixture.AddContent(_incoming, "local-parent", remoteId: "same");
        MedalFixture.AddContent(_incoming, "child", video: @"D:\Capture\edit.mp4");
        using (var db = MedalFixture.Open(_incoming)) MedalFixture.Execute(db, "UPDATE contents SET parent_id='local-parent' WHERE local_content_id='child'");
        var plan = Plan();
        Assert.Empty(plan.Conflicts);
        Assert.Equal("server-parent", Field(plan.Snapshot, "child", "parent_id"));
    }

    [Fact]
    public void DeleteVersusKeepRequiresExplicitReview()
    {
        MedalFixture.AddContent(_shared, "clip");
        MedalFixture.DeleteContent(_shared, "clip");
        MedalFixture.AddContent(_incoming, "clip");
        Assert.Contains(Plan().Conflicts, c => c.Contains("Deletion records", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownEmbeddedAssetsAndAssetsOutsideSourceBlockEnrollment()
    {
        MedalFixture.AddContent(_incoming, "clip", video: @"D:\Elsewhere\clip.mp4", metadata: "{\"futureAsset\":\"D:\\\\Capture\\\\unknown.dat\"}");
        var plan = Plan();
        Assert.Contains(plan.Conflicts, c => c.Contains("outside", StringComparison.Ordinal));
        Assert.Contains(plan.Conflicts, c => c.Contains("unsupported embedded", StringComparison.Ordinal));
    }

    [Fact]
    public void AccountsAndConflictingLocalRemoteIdentitiesAreRefused()
    {
        var other = LibrarySnapshot.Create("99999", Read(_shared).SchemaVersion, Read(_shared).SchemaFingerprint, Read(_shared).Tables);
        Assert.Throws<IncompatibleLibraryException>(() => LibraryReconciliation.Plan(other, Read(_incoming), Source, Destination));
        MedalFixture.AddContent(_shared, "a", remoteId: "remote-a");
        MedalFixture.AddContent(_shared, "b", remoteId: "remote-b");
        MedalFixture.AddContent(_incoming, "a", remoteId: "remote-b");
        Assert.NotEmpty(Plan().Conflicts);
    }

    [Fact]
    public void TwoIncomingAliasesCannotSilentlyCollapseIntoOneSharedRow()
    {
        MedalFixture.AddContent(_shared, "canonical", remoteId: "uploaded");
        MedalFixture.AddContent(_incoming, "canonical");
        MedalFixture.AddContent(_incoming, "another-local-id", remoteId: "uploaded");
        Assert.Contains(Plan().Conflicts, c => c.Contains("multiple incoming entries", StringComparison.Ordinal));
    }

    [Fact]
    public void CandidateRestoresWithRebuiltCountersAndWithoutReplacingDeviceSettings()
    {
        MedalFixture.AddContent(_shared, "old", metadata: "{\"isFavorited\":1}");
        MedalFixture.AddContent(_incoming, "new", metadata: "{\"isFavorited\":1}");
        var snapshot = CountRebuilder.Rebuild(Plan().Snapshot, _incoming);
        string before = MedalFixture.Settings(_incoming);
        LibraryRestorer.Restore(_incoming, snapshot, new RestoreOptions(MedalFixture.AccountId, _fixture.PathFor("backups")), DateTimeOffset.UtcNow);
        Assert.Equal(2, MedalFixture.Count(_incoming, "favorites"));
        Assert.Equal(before, MedalFixture.Settings(_incoming));
    }

    [Fact]
    public void CaptureFolderChangeOnlyTouchesItsExpectedSettingAndWrongExpectationRollsBack()
    {
        string before = MedalFixture.Settings(_incoming);
        Assert.Throws<UnsafeOperationException>(() => CaptureFolderSetting.Change(_incoming, @"D:\Wrong", @"M:\Clips"));
        Assert.Equal(before, MedalFixture.Settings(_incoming));
        CaptureFolderSetting.Change(_incoming, @"M:\Clips", @"N:\Clips");
        string after = MedalFixture.Settings(_incoming);
        Assert.Equal(before.Replace("M:", "N:", StringComparison.Ordinal), after);
        Assert.Equal(Read(_incoming).Digest(), Read(_shared).Digest());
    }

    [Fact]
    public void FileCopiesAreVerifiedAndNeverOverwriteADifferentExistingFileOrDeleteOriginals()
    {
        string source = _fixture.PathFor("source.dat"), target = _fixture.PathFor("new", "target.dat");
        File.WriteAllText(source, "original");
        EnrollmentCopy.Copy(source, target);
        EnrollmentCopy.Copy(source, target);
        Assert.Equal("original", File.ReadAllText(source));
        Assert.Equal("original", File.ReadAllText(target));
        File.WriteAllText(source, "different");
        Assert.Throws<UnsafeOperationException>(() => EnrollmentCopy.Copy(source, target));
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void EnrollmentTransactionPublishesOnlyVerifiedUnionAndRetainsOwnershipAfterCopyFailure(bool conflict)
    {
        if (!OperatingSystem.IsWindows()) return;
        string captureDrive = Enumerable.Range('M', 14).Select(c => (char)c + @":\")
            .First(d => !Directory.Exists(d));
        string root = _fixture.PathFor("source");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "unique.mp4");
        File.WriteAllText(source, "synthetic recording");
        using (var db = MedalFixture.Open(_incoming)) MedalFixture.SetSetting(db, "ClipFolder", System.Text.Json.JsonSerializer.Serialize(root));
        MedalFixture.AddContent(_incoming, "unique", video: source, metadata: "{\"title\":\"Unique\",\"isFavorited\":true}");
        string beforeDigest = Read(_incoming).Digest();
        string settingsBefore = MedalFixture.Settings(_incoming);
        var device = new DeviceIdentity(new string('b', 32), "synthetic second PC");
        var config = new MedalSyncConfig(1, new string('a', 32), MedalFixture.AccountId, _fixture.PathFor("coordination"),
            _fixture.PathFor("server"), captureDrive + "Clips", _fixture.PathFor("cache"), "unused.exe",
            new MountConfig(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []);
        Directory.CreateDirectory(config.ServerCapture.Display);
        File.WriteAllText(config.ServerCapture.Append(MedalSyncConfig.StorageMarker).Display, config.LibraryId);
        var shared = Read(_shared);
        var store = CoordinationStore.Create(config.LibraryCoordinationDirectory, config.LibraryId, shared,
            new AssetManifest(1, [], []), device, new string('c', 32), new MedalVersions("test", "test"), DateTimeOffset.UtcNow);
        var plan = LibraryReconciliation.Plan(shared, Read(_incoming), WindowsPath.Parse(root), config.Capture.Append("Devices", device.DeviceId));
        var candidate = CountRebuilder.Rebuild(plan.Snapshot, _incoming);
        var review = new EnrollmentReview(1, config.LibraryId, device.DeviceId, 0, shared.Digest(), beforeDigest, candidate.Digest(),
            root, config.Capture.Append("Devices", device.DeviceId).Display, plan.Added, plan.Matched, plan.Identities, plan.Files, plan.Conflicts);
        var install = new MedalInstall(_fixture.PathFor("fake-install"), _incoming, MedalFixture.AccountId,
            _fixture.PathFor("fake-install", "Medal.exe"), new MedalVersions("test", "test"));
        string evidence = _fixture.PathFor("enrollment-evidence");
        Directory.CreateDirectory(evidence);
        string target = WindowsPath.Parse(Assert.Single(plan.Files).CapturePath).Rebase(config.Capture, config.ServerCapture).Display;
        if (conflict) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "different existing file"); }
        if (conflict)
        {
            Assert.Throws<UnsafeOperationException>(() => EnrollmentCommands.Apply(config, install, new LocalState(_fixture.PathFor("state")),
                store, device, review, candidate, evidence, new StringWriter()));
            Assert.Equal(OwnershipState.RecoveryRequired, store.ReadJournal().State);
            Assert.Equal(0, store.ReadJournal().CommittedGeneration);
            Assert.Equal(beforeDigest, Read(_incoming).Digest());
            Assert.Equal(settingsBefore, MedalFixture.Settings(_incoming));
            Assert.Equal("different existing file", File.ReadAllText(target));
        }
        else
        {
            Assert.Equal(0, EnrollmentCommands.Apply(config, install, new LocalState(_fixture.PathFor("state")), store, device,
                review, candidate, evidence, new StringWriter()));
            Assert.Equal(OwnershipState.Clean, store.ReadJournal().State);
            Assert.Equal(1, store.ReadJournal().CommittedGeneration);
            Assert.Equal(candidate.Digest(), Read(_incoming).Digest());
            Assert.Equal(candidate.Digest(), store.Generations.Read(1).Snapshot.Digest());
            Assert.Equal("synthetic recording", File.ReadAllText(target));
            Assert.Contains("incoming-microphone", MedalFixture.Settings(_incoming), StringComparison.Ordinal);
            using var db = LibraryDatabase.OpenReadOnly(_incoming);
            Assert.Contains(config.Capture.Root[..2], LibraryDatabase.ReadSetting(db, null, "ClipFolder"), StringComparison.Ordinal);
        }
        Assert.Equal("synthetic recording", File.ReadAllText(source));
        Assert.NotEmpty(DatabaseBackup.List(evidence, MedalFixture.AccountId));
        Assert.True(File.Exists(Path.Combine(evidence, "apply.json")));
    }
}

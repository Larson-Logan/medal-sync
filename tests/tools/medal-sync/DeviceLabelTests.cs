using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class DeviceLabelTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private static MedalSyncConfig Config => new(1, "0123456789abcdef0123456789abcdef", "12345", @"D:\Coordination", @"D:\Media",
        @"M:\Clips", @"E:\Cache", @"C:\Programs\rclone.exe", new(5, 20, 20, 300, 30, 1000, 2, 53684), 4, []) { StorageMode = StorageMode.Local };

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void LabelsChangePublishedTextWithoutChangingIdentityOrCopiedProfileGuard()
    {
        var desk = new LocalState(_fixture.PathFor("desk"));
        var livingRoom = new LocalState(_fixture.PathFor("living-room"));
        var original = desk.Device("SAME-PC (alex)");
        livingRoom.Device("SAME-PC (alex)");
        byte[] identity = File.ReadAllBytes(Path.Combine(desk.Root, "device.json"));
        File.WriteAllBytes(desk.ConfigPath, Json.Serialize(Config with { DeviceLabel = "Desk PC" }));
        File.WriteAllBytes(livingRoom.ConfigPath, Json.Serialize(Config with { DeviceLabel = "Living room" }));
        var a = desk.Device("SAME-PC (alex)");
        var b = livingRoom.Device("SAME-PC (alex)");
        Assert.Equal(original.DeviceId, a.DeviceId);
        Assert.NotEqual(a.DeviceId, b.DeviceId);
        Assert.Equal(identity, File.ReadAllBytes(Path.Combine(desk.Root, "device.json")));
        var owned = Ownership.Acquire(Ownership.Initial(Config.LibraryId, Config.AccountId, DateTimeOffset.UtcNow),
            a, new string('a', 32), DateTimeOffset.UtcNow);
        Assert.Contains("Desk PC", StatusText.For(owned, b));
        File.WriteAllBytes(desk.ConfigPath, Json.Serialize(Config with { DeviceLabel = "New desk name" }));
        var renamed = desk.Device("SAME-PC (alex)");
        Assert.True(owned.IsOwnedBy(renamed, new string('a', 32)));
        Assert.Equal(OwnershipState.Active, Ownership.MarkActive(owned, renamed, new string('a', 32), DateTimeOffset.UtcNow).State);
        Assert.Throws<UnsafeOperationException>(() => desk.Device("COPIED-PC (alex)"));
        Assert.Equal("Living room", b.DisplayName);
    }

    [Fact]
    public void UnsetLabelsStayAbsentAndDuplicateNamesSuggestALabel()
    {
        string json = System.Text.Encoding.UTF8.GetString(Json.Serialize(Config));
        Assert.DoesNotContain("deviceLabel", json);
        Assert.Null(Json.Deserialize<MedalSyncConfig>(System.Text.Encoding.UTF8.GetBytes(json), "old config").DeviceLabel);
        var me = new DeviceIdentity(new string('a', 32), "SAME-PC (alex)");
        Assert.Null(DeviceNameAdvice.For(Config, me, [me]));
        Assert.NotNull(DeviceNameAdvice.For(Config, me, [new(new string('b', 32), me.DisplayName)]));
        Assert.Null(DeviceNameAdvice.For(Config with { DeviceLabel = "Desk" }, me, [new(new string('b', 32), me.DisplayName)]));
    }

    [Fact]
    public void UnreadableHistoricalManifestDoesNotPreventNameAdvice()
    {
        var state = new LocalState(_fixture.PathFor("name-advice"));
        var device = state.Device();
        var snapshot = LibraryDatabase.ReadSnapshot(_fixture.CreateLibrary("advice-db"), Config.AccountId);
        var assets = new AssetManifest(1, [], []);
        var versions = new MedalVersions(null, null);
        var now = DateTimeOffset.UtcNow;
        var store = CoordinationStore.Create(_fixture.PathFor("coordination"), Config.LibraryId, snapshot, assets,
            device, new string('1', 32), versions, now);
        string damaged = Path.Combine(store.Generations.Directory, GenerationStore.Name(1));
        Directory.CreateDirectory(damaged);
        File.WriteAllText(Path.Combine(damaged, "manifest.json"), "truncated manifest");
        Assert.Null(DeviceNameAdvice.Read(Config, state, store));

        var duplicate = device with { DeviceId = new string('b', 32) };
        store.Generations.Publish(2, 0, new string('2', 32), duplicate, snapshot, assets, versions, now);
        Assert.NotNull(DeviceNameAdvice.Read(Config, state, store));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Desk\r\nPC")]
    [InlineData("Desk\tPC")]
    [InlineData("Desk\u2028PC")]
    [InlineData("Desk\u202ePC")]
    public void InvalidLabelsAreRefused(string label) => Assert.Throws<InvalidDataException>(() => (Config with { DeviceLabel = label }).Validate());

    [Fact]
    public void LabelsHaveABoundedLength()
    {
        (Config with { DeviceLabel = new string('a', 64) }).Validate();
        Assert.Throws<InvalidDataException>(() => (Config with { DeviceLabel = new string('a', 65) }).Validate());
    }
}

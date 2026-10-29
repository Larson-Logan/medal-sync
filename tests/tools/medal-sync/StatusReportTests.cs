using System.Runtime.Versioning;
using System.Text.Json;
using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary><c>status --json</c>: a versioned, read-only document that follows the tray's rules.</summary>
public sealed class StatusReportTests : IDisposable
{
    private static readonly DeviceIdentity First = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "First PC");
    private static readonly DeviceIdentity Second = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Second PC");
    private const string LibraryId = "0123456789abcdef0123456789abcdef";
    private const string SessionA = "11111111111111111111111111111111";

    private readonly MedalFixture _fixture = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public void Dispose() => _fixture.Dispose();

    private LocalState State => new(_fixture.PathFor("state"));

    private CoordinationStore NewStore()
    {
        string db = _fixture.CreateLibrary("pc-" + Guid.NewGuid().ToString("N"));
        return CoordinationStore.Create(_fixture.PathFor("coordination", LibraryId), LibraryId, LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId),
            new(AssetManifest.CurrentFormat, [], []), First, SessionA, new("2643.583.1", "2643.3040.1"), _now);
    }

    private JsonElement Report(CoordinationStore store, DeviceIdentity? device, bool supervisorRunning)
    {
        var journal = store.ReadJournal();
        var desktop = DesktopStatus.Read(State, LibraryId, journal, device, supervisorRunning, _now);
        var report = StatusReport.Reached(LibraryId, journal, device, desktop, store.PendingRequests(_now), store.StaleRequests(_now),
            StorageReplication.Pending(State, LibraryId), StatusReport.ReadLastVerifiedAt(State.Root, LibraryId), _now);
        using var document = JsonDocument.Parse(report.ToJson());
        return document.RootElement.Clone();
    }

    private void Heartbeat(SessionPhase phase, DateTimeOffset at)
    {
        Directory.CreateDirectory(State.Root);
        File.WriteAllBytes(Path.Combine(State.Root, "desktop-status.json"), Json.Serialize(new ProgressEnvelope(1, LibraryId, MedalFixture.AccountId,
            First.DeviceId, at, new(phase, "Synthetic next action", SessionA))));
    }

    [Fact]
    public void Ready_IsSafeToSwitchAndCarriesTheFormatAndJournal()
    {
        var store = NewStore();
        var status = Report(store, First, supervisorRunning: false);
        Assert.Equal(StatusReport.CurrentFormat, status.GetProperty("format").GetInt32());
        Assert.Equal(LibraryId, status.GetProperty("libraryId").GetString());
        Assert.Equal("reachable", status.GetProperty("storage").GetString());
        Assert.Equal("READY", status.GetProperty("phase").GetString());
        Assert.Equal("Ready", status.GetProperty("title").GetString());
        Assert.True(status.GetProperty("safeToSwitch").GetBoolean());
        var journal = status.GetProperty("journal");
        Assert.Equal("CLEAN", journal.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, journal.GetProperty("owner").ValueKind);
        Assert.False(journal.GetProperty("ownedByThisPc").GetBoolean());
        Assert.Equal(0, journal.GetProperty("committedGeneration").GetInt64());
        Assert.Empty(status.GetProperty("handoffRequests").EnumerateArray());
        Assert.Empty(status.GetProperty("pendingCloudCopies").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastVerifiedAt").ValueKind);
    }

    // A supervisor preparing a start has not acquired the journal yet; it must never read as safe.
    [Fact]
    public void CleanJournalWithARunningSupervisor_IsStartingOrUnknownAndNeverSafe()
    {
        var store = NewStore();
        var unknown = Report(store, First, supervisorRunning: true);
        Assert.Equal("UNKNOWN", unknown.GetProperty("phase").GetString());
        Assert.False(unknown.GetProperty("safeToSwitch").GetBoolean());

        Heartbeat(SessionPhase.Starting, _now);
        var starting = Report(store, First, supervisorRunning: true);
        Assert.Equal("STARTING", starting.GetProperty("phase").GetString());
        Assert.False(starting.GetProperty("safeToSwitch").GetBoolean());
    }

    [Fact]
    public void OwnedByAnotherPc_IsWaitingAndNamesTheOwnerWithoutDeviceIds()
    {
        var store = NewStore();
        store.Update(r => Ownership.Acquire(r, Second, SessionA, _now), TimeSpan.FromSeconds(2));
        store.Update(r => Ownership.MarkActive(r, Second, SessionA, _now), TimeSpan.FromSeconds(2));
        var status = Report(store, First, supervisorRunning: false);
        Assert.Equal("WAITING", status.GetProperty("phase").GetString());
        Assert.False(status.GetProperty("safeToSwitch").GetBoolean());
        var journal = status.GetProperty("journal");
        Assert.Equal("ACTIVE", journal.GetProperty("state").GetString());
        Assert.Equal("Second PC", journal.GetProperty("owner").GetString());
        Assert.False(journal.GetProperty("ownedByThisPc").GetBoolean());
        Assert.Equal(SessionA, journal.GetProperty("sessionId").GetString());

        string json = status.GetRawText();
        Assert.DoesNotContain(First.DeviceId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Second.DeviceId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(_fixture.Root, json, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnedHereAndActive_FollowsAFreshHeartbeatAndIsUnknownWithoutOne()
    {
        var store = NewStore();
        store.Update(r => Ownership.Acquire(r, First, SessionA, _now), TimeSpan.FromSeconds(2));
        store.Update(r => Ownership.MarkActive(r, First, SessionA, _now), TimeSpan.FromSeconds(2));

        Heartbeat(SessionPhase.Active, _now);
        var active = Report(store, First, supervisorRunning: true);
        Assert.Equal("ACTIVE", active.GetProperty("phase").GetString());
        Assert.True(active.GetProperty("journal").GetProperty("ownedByThisPc").GetBoolean());
        Assert.False(active.GetProperty("safeToSwitch").GetBoolean());

        // A stale or unreadable heartbeat is an explicit unknown, never Ready.
        Heartbeat(SessionPhase.Active, _now - TimeSpan.FromMinutes(5));
        Assert.Equal("UNKNOWN", Report(store, First, supervisorRunning: true).GetProperty("phase").GetString());
        File.WriteAllText(Path.Combine(State.Root, "desktop-status.json"), "{ not json");
        Assert.Equal("UNKNOWN", Report(store, First, supervisorRunning: true).GetProperty("phase").GetString());

        // No supervisor here: this PC's own unfinished session needs recovery.
        var stopped = Report(store, First, supervisorRunning: false);
        Assert.Equal("RECOVERY_NEEDED", stopped.GetProperty("phase").GetString());
        Assert.False(stopped.GetProperty("safeToSwitch").GetBoolean());
    }

    [Fact]
    public void PcWithoutAnIdentity_OwnsNothingAndNoIdentityIsCreated()
    {
        var store = NewStore();
        store.Update(r => Ownership.Acquire(r, First, SessionA, _now), TimeSpan.FromSeconds(2));
        var status = Report(store, device: null, supervisorRunning: false);
        Assert.Equal("WAITING", status.GetProperty("phase").GetString());
        Assert.False(status.GetProperty("journal").GetProperty("ownedByThisPc").GetBoolean());
        Assert.False(File.Exists(Path.Combine(State.Root, "device.json")));
        Assert.False(Directory.Exists(Path.Combine(store.Root, "requests")));
    }

    [Fact]
    public void PendingRequestsCloudCopiesAndLastVerification_AreReportedWithTheirTimes()
    {
        var store = NewStore();
        store.WriteRequest(new HandoffRequest(1, Second, new string('d', 32), _now - TimeSpan.FromMinutes(10)));
        var third = new DeviceIdentity(new string('e', 32), "Third PC");
        store.WriteRequest(new HandoffRequest(1, third, new string('f', 32), _now - TimeSpan.FromHours(7)));

        var first = _now - TimeSpan.FromDays(2);
        Directory.CreateDirectory(Path.Combine(State.Root, "replication"));
        File.WriteAllBytes(Path.Combine(State.Root, "replication", "pending.json"),
            Json.Serialize(new PendingReplicas(1, LibraryId, [new("offsite", first, _now - TimeSpan.FromHours(1))])));
        var verified = _now - TimeSpan.FromHours(1);
        File.WriteAllBytes(Path.Combine(State.Root, "replication", "last-verified.json"), Json.Serialize(new
        {
            format = 1, libraryId = LibraryId, snapshotDigest = "x", verifiedAt = verified, files = 3, localCopy = true,
            primary = @"\\server\private\Medal", targets = new[] { "required" }, deferred = new[] { "offsite" },
        }));

        var status = Report(store, First, supervisorRunning: false);
        var requests = status.GetProperty("handoffRequests").EnumerateArray().ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("Third PC", requests[0].GetProperty("device").GetString());
        Assert.True(requests[0].GetProperty("stale").GetBoolean());
        Assert.Equal("Second PC", requests[1].GetProperty("device").GetString());
        Assert.False(requests[1].GetProperty("stale").GetBoolean());
        Assert.Equal(600, requests[1].GetProperty("ageSeconds").GetInt64());

        var copy = Assert.Single(status.GetProperty("pendingCloudCopies").EnumerateArray());
        Assert.Equal("offsite", copy.GetProperty("name").GetString());
        Assert.Equal(first, copy.GetProperty("firstFailure").GetDateTimeOffset());
        Assert.Equal(verified, status.GetProperty("lastVerifiedAt").GetDateTimeOffset());
        // The receipt's primary location is private and not needed here.
        Assert.DoesNotContain("private", status.GetRawText(), StringComparison.Ordinal);

        // A receipt for another library or in an unexpected shape is not this library's verification.
        File.WriteAllText(Path.Combine(State.Root, "replication", "last-verified.json"), "{\"format\":1,\"libraryId\":\"" + new string('9', 32) + "\",\"verifiedAt\":\"2026-01-01T00:00:00Z\"}");
        Assert.Null(StatusReport.ReadLastVerifiedAt(State.Root, LibraryId));
        File.WriteAllText(Path.Combine(State.Root, "replication", "last-verified.json"), "[1]");
        Assert.Null(StatusReport.ReadLastVerifiedAt(State.Root, LibraryId));
        File.WriteAllText(Path.Combine(State.Root, "replication", "last-verified.json"), "{");
        Assert.Null(StatusReport.ReadLastVerifiedAt(State.Root, LibraryId));
    }

    [Fact]
    public void Offline_KeepsLocalFactsAndLeavesOwnershipUnknown()
    {
        Directory.CreateDirectory(Path.Combine(State.Root, "replication"));
        File.WriteAllBytes(Path.Combine(State.Root, "replication", "pending.json"),
            Json.Serialize(new PendingReplicas(1, LibraryId, [new("offsite", _now, _now)])));
        var report = StatusReport.Offline(LibraryId, "Waiting for storage.", StorageReplication.Pending(State, LibraryId), null, _now);
        using var document = JsonDocument.Parse(report.ToJson());
        var status = document.RootElement;
        Assert.Equal("unreachable", status.GetProperty("storage").GetString());
        Assert.Equal("Waiting for storage.", status.GetProperty("message").GetString());
        Assert.Equal("WAITING_FOR_STORAGE", status.GetProperty("phase").GetString());
        Assert.False(status.GetProperty("safeToSwitch").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("journal").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("handoffRequests").ValueKind);
        Assert.Single(status.GetProperty("pendingCloudCopies").EnumerateArray());
    }

    private MedalSyncConfig StatusConfig(string coordinationRoot) =>
        new(1, LibraryId, MedalFixture.AccountId, coordinationRoot, _fixture.PathFor("media"), @"M:\Clips", _fixture.PathFor("cache"),
            @"C:\Tools\rclone.exe", new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []) { StorageMode = StorageMode.Local };

    [Fact]
    [SupportedOSPlatform("windows")]
    public void StatusCommand_WritesTheDocumentAndExitsThreeWithABodyWhenOffline()
    {
        if (!OperatingSystem.IsWindows()) return;
        var offline = new StringWriter();
        Assert.Equal(3, SessionCommands.WriteStatus(StatusConfig(_fixture.PathFor("offline")), State, First, json: true, offline, () => false));
        using (var document = JsonDocument.Parse(offline.ToString()))
        {
            Assert.Equal("unreachable", document.RootElement.GetProperty("storage").GetString());
            Assert.Equal(StatusReport.CurrentFormat, document.RootElement.GetProperty("format").GetInt32());
        }

        var store = NewStore();
        var output = new StringWriter();
        Assert.Equal(0, SessionCommands.WriteStatus(StatusConfig(_fixture.PathFor("coordination")), State, null, json: true, output, () => false));
        using (var document = JsonDocument.Parse(output.ToString()))
        {
            Assert.True(document.RootElement.GetProperty("safeToSwitch").GetBoolean());
            Assert.Equal("READY", document.RootElement.GetProperty("phase").GetString());
        }
        Assert.False(File.Exists(Path.Combine(State.Root, "device.json")));
        Assert.Equal(1, store.ReadJournal().Revision);
    }
}

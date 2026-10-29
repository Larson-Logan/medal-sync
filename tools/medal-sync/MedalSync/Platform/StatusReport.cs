using System.Text.Json;
using MedalSync.Coordination;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>The journal as <c>status --json</c> shows it: no device IDs and no paths.</summary>
internal sealed record StatusJournal(OwnershipState State, string? Owner, bool OwnedByThisPc, string? SessionId,
    long CommittedGeneration, long Revision, DateTimeOffset UpdatedAt, string? Reason);

internal sealed record StatusHandoffRequest(string Device, DateTimeOffset RequestedAt, long AgeSeconds, bool Stale);

internal sealed record StatusCloudCopy(string Name, DateTimeOffset FirstFailure, DateTimeOffset LastAttempt);

/// <summary>
/// The machine-readable answer to "who holds the library, and is it safe to switch PCs?". Documented in docs/storage.md.
/// Changes within one <see cref="Format"/> only add fields. Phase, title and next action follow the tray's rules exactly.
/// Lists are null when they could not be read (storage offline), never an empty list that pretends nothing is pending.
/// </summary>
internal sealed record StatusReport(
    int Format,
    string LibraryId,
    string Storage,
    string? Message,
    StatusJournal? Journal,
    SessionPhase Phase,
    string Title,
    string NextAction,
    bool SafeToSwitch,
    IReadOnlyList<StatusHandoffRequest>? HandoffRequests,
    IReadOnlyList<StatusCloudCopy> PendingCloudCopies,
    DateTimeOffset? LastVerifiedAt,
    DateTimeOffset ObservedAt)
{
    public const int CurrentFormat = 1;

    public const string Reachable = "reachable";

    public const string Unreachable = "unreachable";

    /// <summary>
    /// <paramref name="device"/> is null when this PC has no identity yet; it then owns nothing.
    /// Only a Ready phase, which in turn needs a Clean journal and no supervisor preparing a start here, is safe to switch.
    /// </summary>
    public static StatusReport Reached(string libraryId, OwnershipRecord journal, DeviceIdentity? device, DesktopStatus desktop,
        IEnumerable<HandoffRequest> pending, IEnumerable<HandoffRequest> stale, IReadOnlyList<PendingReplica> cloudCopies,
        DateTimeOffset? lastVerifiedAt, DateTimeOffset now)
    {
        bool mine = device is not null && journal.Owner?.DeviceId == device.DeviceId;
        var requests = pending.Select(r => Request(r, stale: false, now)).Concat(stale.Select(r => Request(r, stale: true, now)))
            .OrderBy(r => r.RequestedAt).ToArray();
        bool safe = journal.State == OwnershipState.Clean && desktop.Phase == SessionPhase.Ready;
        return new(CurrentFormat, libraryId, Reachable, null,
            new(journal.State, journal.Owner?.DisplayName, mine, journal.SessionId, journal.CommittedGeneration, journal.Revision,
                journal.UpdatedAt, journal.Reason),
            desktop.Phase, desktop.Title, desktop.NextAction, safe, requests, CloudCopies(cloudCopies), lastVerifiedAt, now);
    }

    /// <summary>The journal cannot be read. Local facts are still reported; nothing about ownership is guessed.</summary>
    public static StatusReport Offline(string libraryId, string message, IReadOnlyList<PendingReplica> cloudCopies,
        DateTimeOffset? lastVerifiedAt, DateTimeOffset now)
    {
        var desktop = DesktopStatus.StorageOffline(now);
        return new(CurrentFormat, libraryId, Unreachable, message, null, desktop.Phase, desktop.Title, desktop.NextAction,
            SafeToSwitch: false, HandoffRequests: null, CloudCopies(cloudCopies), lastVerifiedAt, now);
    }

    private static StatusHandoffRequest Request(HandoffRequest request, bool stale, DateTimeOffset now) =>
        new(request.Device.DisplayName, request.RequestedAt, Math.Max(0, (long)(now - request.RequestedAt).TotalSeconds), stale);

    private static StatusCloudCopy[] CloudCopies(IReadOnlyList<PendingReplica> pending) =>
        pending.Select(p => new StatusCloudCopy(p.Name, p.Since, p.LastAttempt)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// When <c>replication/last-verified.json</c> last proved a copy of this library. Null when there is none,
    /// it is for another library, or it cannot be read. Only the time is taken; the receipt also names a primary path.
    /// </summary>
    internal static DateTimeOffset? ReadLastVerifiedAt(string stateRoot, string libraryId)
    {
        string path = Path.Combine(stateRoot, "replication", "last-verified.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out int f) || f != 1
                || !root.TryGetProperty("libraryId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != libraryId
                || !root.TryGetProperty("verifiedAt", out var at) || at.ValueKind != JsonValueKind.String || !at.TryGetDateTimeOffset(out var verified))
                return null;
            return verified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);
}

using System.Globalization;
using System.Text.Json;
using MedalSync.Config;

namespace MedalSync.Platform;

/// <summary>Counts and outcomes only; no media paths, destination or snapshot contents.</summary>
internal sealed record CloudCopyCheck(string Result, string? Reason, DateTimeOffset FinishedAt, long Generation,
    int Planned, int Checked, int Matched, int Missing, int Mismatched, int Unknown, long Bytes);

internal sealed record CloudCopyCheckStatus(string Name, CloudCopyCheck? LastCheck, CloudCopyCheck? LastComplete,
    bool NeedsAttention)
{
    // Kept out of the JSON contract: these select notices without changing the historical results.
    internal bool Failed { get; init; }
    internal bool Incomplete { get; init; }

    public IEnumerable<string> Messages()
    {
        if (!NeedsAttention) yield break;
        var failure = LastComplete?.Result == ReplicaAudit.Failed ? LastComplete : LastCheck?.Result == ReplicaAudit.Failed ? LastCheck : null;
        if (Failed && failure is not null)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"Cloud copy '{Name}' failed its last complete check on {failure.FinishedAt:yyyy-MM-dd HH:mm} UTC ({failure.Missing} missing, {failure.Mismatched} different). The next handoff reads every referenced file back.");
        if (Incomplete && LastCheck is { Result: ReplicaAudit.Incomplete } incomplete)
        {
            string action = incomplete.Reason switch
            {
                "sign-in" => "Sign in again with rclone, then check the copy again.",
                "offline" => "Reconnect storage, then check the copy again.",
                "configuration" => "Check the rclone configuration, then check the copy again.",
                "canceled" or "library-changed" => "Check the copy again while the library is Ready.",
                _ => "Check storage and run the check again while Ready.",
            };
            yield return string.Create(CultureInfo.InvariantCulture,
                $"Cloud copy '{Name}' last check on {incomplete.FinishedAt:yyyy-MM-dd HH:mm} UTC was incomplete ({incomplete.Reason ?? "error"}). {action}");
        }
    }
}

/// <summary>Advisory local check history. A successful handoff clears warnings only for targets that it verified.</summary>
internal static class ReplicaCheckStatus
{
    internal static readonly string[] Reasons = ["offline", "sign-in", "configuration", "canceled", "library-changed", "error"];

    public static IReadOnlyList<CloudCopyCheckStatus> Read(LocalState state, string libraryId, DateTimeOffset now,
        IReadOnlyList<ReplicaTarget>? configured = null)
    {
        ReplicaAuditHistory history;
        try { history = ReplicaAudit.Read(state, libraryId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            history = new(ReplicaAuditHistory.CurrentFormat, libraryId, []);
        }
        var names = (configured?.Select(t => t.Name) ?? history.Targets.Select(t => t.Target))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var (verifiedAt, verifiedTargets) = Verified(state, libraryId, now);
        return names.Select(name =>
        {
            var target = history.Targets.FirstOrDefault(t => t.Target.Equals(name, StringComparison.OrdinalIgnoreCase));
            var complete = Check(target?.LastComplete, name, now);
            var last = target?.Attempts.Select(a => Check(a, name, now)).Where(a => a is not null)
                .OrderByDescending(a => a!.FinishedAt).FirstOrDefault() ?? complete;
            bool Unresolved(CloudCopyCheck? check) => check is { Result: not ReplicaAudit.Passed }
                && !(verifiedTargets.Contains(name) && verifiedAt >= check.FinishedAt);
            bool failed = complete?.Result == ReplicaAudit.Failed && Unresolved(complete)
                || last?.Result == ReplicaAudit.Failed && Unresolved(last);
            bool incomplete = last?.Result == ReplicaAudit.Incomplete && Unresolved(last);
            return new CloudCopyCheckStatus(name, last, complete, failed || incomplete) { Failed = failed, Incomplete = incomplete };
        }).ToArray();
    }

    private static CloudCopyCheck? Check(ReplicaAuditAttempt? attempt, string target, DateTimeOffset now)
    {
        if (attempt is not { } a || !string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase)
            || a.Result is not (ReplicaAudit.Passed or ReplicaAudit.Failed or ReplicaAudit.Incomplete)
            || a.Reason is not null && !Reasons.Contains(a.Reason, StringComparer.Ordinal)
            || a.FinishedAt < a.StartedAt || a.FinishedAt > now.AddSeconds(5) || a.StartedAt == default || a.Generation < 0
            || a.Planned < 0 || a.Checked < 0 || a.Matched < 0 || a.Missing < 0 || a.Mismatched < 0 || a.Unknown < 0 || a.Bytes < 0
            || (long)a.Matched + a.Missing + a.Mismatched + a.Unknown != a.Planned
            || (long)a.Matched + a.Missing + a.Mismatched != a.Checked
            || a.Result == ReplicaAudit.Passed && (a.Missing != 0 || a.Mismatched != 0 || a.Unknown != 0)
            || a.Result == ReplicaAudit.Failed && a.Missing == 0 && a.Mismatched == 0) return null;
        return new(a.Result, a.Reason, a.FinishedAt, a.Generation, a.Planned, a.Checked, a.Matched,
            a.Missing, a.Mismatched, a.Unknown, a.Bytes);
    }

    private static (DateTimeOffset? At, HashSet<string> Targets) Verified(LocalState state, string libraryId, DateTimeOffset now)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(state.Root, "replication", "last-verified.json")));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || !format.TryGetInt32(out int f) || f != 1
                || !root.TryGetProperty("libraryId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != libraryId
                || !root.TryGetProperty("verifiedAt", out var at) || at.ValueKind != JsonValueKind.String || !at.TryGetDateTimeOffset(out var time)
                || time > now.AddSeconds(5) || time == default
                || !root.TryGetProperty("targets", out var names) || names.ValueKind != JsonValueKind.Array
                || names.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.String)) return (null, targets);
            foreach (var name in names.EnumerateArray()) targets.Add(name.GetString()!);
            return (time, targets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (null, targets);
        }
    }
}

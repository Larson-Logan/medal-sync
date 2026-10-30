using System.Globalization;
using System.Text;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>The journal moved on while a Ready operation ran; its result describes a generation that is no longer committed.</summary>
internal sealed class LibraryChangedException(string message) : OperationCanceledException(message);

internal sealed record ReplicaAuditTarget(string Name, bool Required, string Destination);

/// <summary>What a check would read back: the committed generation's referenced media, from this PC's source, for each target.</summary>
internal sealed record ReplicaAuditPlan(long Generation, long Revision, string SnapshotDigest, string Source, int Files, long Bytes,
    IReadOnlyList<ReplicaAuditTarget> Targets);

/// <summary>One check of one target. Counts only: no paths, so the history can be read safely.</summary>
/// <param name="Result"><c>passed</c>, <c>failed</c> (missing or different files) or <c>incomplete</c>.</param>
/// <param name="Reason">Why an attempt is incomplete: offline, sign-in, configuration, canceled, library-changed or error.</param>
internal sealed record ReplicaAuditAttempt(string Target, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, long Generation,
    string SnapshotDigest, string Result, string? Reason, int Planned, int Checked, int Matched, int Missing, int Mismatched, int Unknown, long Bytes)
{
    public bool Complete => Result is ReplicaAudit.Passed or ReplicaAudit.Failed;
}

internal sealed record ReplicaAuditTargetHistory(string Target, ReplicaAuditAttempt? LastComplete, ReplicaAuditAttempt? LastPassed,
    IReadOnlyList<ReplicaAuditAttempt> Attempts);

internal sealed record ReplicaAuditHistory(int Format, string LibraryId, IReadOnlyList<ReplicaAuditTargetHistory> Targets)
{
    public const int CurrentFormat = 1;
}

/// <summary>
/// An explicit, verify-only check of configured replica targets against the committed generation while the library is
/// Ready. It never copies, overwrites, deletes, restores or takes ownership, and it never writes the handoff
/// verification receipt: an old successful check is no substitute for the fresh verification before handoff.
/// A target found missing or different files loses its incremental receipt (so the next handoff does a full pass);
/// an optional one is also queued for <c>replicate retry</c>.
/// </summary>
internal sealed class ReplicaAudit(MedalSyncConfig config, LocalState state, CoordinationStore store, Action requireIdle,
    IReplicaRemote remote, Action<string>? progress = null, Func<DateTimeOffset>? clock = null)
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Incomplete = "incomplete";
    private const int KeptAttempts = 20;
    private readonly IFileProbe _probe = new FileSystemProbe();
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public static string HistoryPath(LocalState state) => Path.Combine(state.Root, "replication", "audit-history.json");

    private WindowsPath Source => config.PersistentMediaRoot is null ? config.ServerCapture : config.MountedCapture;

    public ReplicaAuditPlan Plan(string? targetName = null)
    {
        requireIdle();
        var journal = store.ReadJournal();
        if (journal.State != OwnershipState.Clean) throw new UnsafeOperationException("Checking cloud copies requires a Ready library. Finish or recover the current session first.");
        var configured = config.Replication?.Targets ?? [];
        if (configured.Count == 0) throw new UnsafeOperationException("No cloud copies are configured.");
        var targets = configured.Where(t => targetName is null || t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            .Select(t => new ReplicaAuditTarget(t.Name, t.Required, ReplicationPlan.Destination(t, config.LibraryId))).ToArray();
        if (targets.Length == 0) throw new UnsafeOperationException("--target must name a configured cloud copy.");
        var snapshot = store.Generations.Read(journal.CommittedGeneration).Snapshot;
        var (files, bytes) = Files(snapshot);
        return new(journal.CommittedGeneration, journal.Revision, snapshot.Digest(), Source.Display, files.Count, bytes, targets);
    }

    /// <summary>Checks each planned target in turn and records every attempt. Cancellation records the attempt in progress, then stops.</summary>
    public IReadOnlyList<ReplicaAuditAttempt> Apply(ReplicaAuditPlan plan)
    {
        void Guard()
        {
            requireIdle();
            var current = store.ReadJournal();
            if (current.State != OwnershipState.Clean || current.Revision != plan.Revision || current.CommittedGeneration != plan.Generation)
                throw new LibraryChangedException("The library changed or another PC started; the check no longer describes the committed generation.");
        }
        Guard();
        var snapshot = store.Generations.Read(plan.Generation).Snapshot;
        if (snapshot.Digest() != plan.SnapshotDigest) throw new InvalidDataException("The committed snapshot differs from the reviewed check.");
        var (files, bytes) = Files(snapshot);
        if (files.Count != plan.Files || bytes != plan.Bytes || Source.Display != plan.Source)
            throw new UnsafeOperationException("Media changed since the preview; preview the check again.");
        var attempts = new List<ReplicaAuditAttempt>();
        foreach (var target in plan.Targets)
        {
            var started = Now;
            ReplicaAuditAttempt attempt;
            try
            {
                Guard();
                Report($"Checking '{target.Name}': reading back {files.Count} file(s), {StorageVolume.Gb(bytes)}.");
                var outcome = remote.Check(plan.Source, target.Destination, files);
                Guard();
                attempt = Summarize(target, plan, started, files, outcome);
            }
            catch (OperationCanceledException ex)
            {
                Record(Attempt(target, plan, started, files.Count, Incomplete, ex is LibraryChangedException ? "library-changed" : "canceled"));
                throw;
            }
            catch (RcloneFailedException ex)
            {
                attempt = Attempt(target, plan, started, files.Count, Incomplete, Reason(ex));
            }
            Record(attempt);
            if (attempt.Missing + attempt.Mismatched > 0) Distrust(target, plan.Source, plan.SnapshotDigest);
            attempts.Add(attempt);
        }
        return attempts;
    }

    private (IReadOnlyList<string> Files, long Bytes) Files(LibrarySnapshot snapshot)
    {
        var source = Source;
        if (File.ReadAllText(source.Append(MedalSyncConfig.StorageMarker).Display).Trim() != config.LibraryId)
            throw new IOException("Storage belongs to another library; the check was refused.");
        var files = ReplicationPlan.Files(snapshot, config.Capture, source, _probe);
        long bytes = files.Sum(f => _probe.Probe(source.Append(f.Split('/')), source).Length);
        return (files, bytes);
    }

    private ReplicaAuditAttempt Summarize(ReplicaAuditTarget target, ReplicaAuditPlan plan, DateTimeOffset started,
        IReadOnlyList<string> files, RemoteCheckOutcome outcome)
    {
        int matched = 0, missing = 0, mismatched = 0;
        foreach (string file in files)
        {
            if (!outcome.Results.TryGetValue(file, out var result)) continue;
            if (result == RemoteCheck.Match) matched++;
            else if (result == RemoteCheck.Missing) missing++;
            else if (result == RemoteCheck.Differs) mismatched++;
        }
        int unknown = files.Count - matched - missing - mismatched;
        string verdict = outcome.Failure is not null ? Incomplete : missing + mismatched > 0 ? Failed : unknown > 0 ? Incomplete : Passed;
        string? reason = outcome.Failure is { } failure ? Reason(failure) : verdict == Incomplete ? "error" : null;
        return new(target.Name, started, Now, plan.Generation, plan.SnapshotDigest, verdict, reason, files.Count,
            files.Count - unknown, matched, missing, mismatched, unknown, plan.Bytes);
    }

    private ReplicaAuditAttempt Attempt(ReplicaAuditTarget target, ReplicaAuditPlan plan, DateTimeOffset started, int planned, string result, string reason) =>
        new(target.Name, started, Now, plan.Generation, plan.SnapshotDigest, result, reason, planned, 0, 0, 0, 0, planned, plan.Bytes);

    private static string Reason(RcloneFailedException ex) =>
        RcloneFailure.IsTransient(ex) ? "offline"
        : ex.Hints.HasFlag(RcloneHints.SignIn) ? "sign-in"
        : ex.Hints.HasFlag(RcloneHints.Config) || ex.ExitCode is null && !ex.TimedOut ? "configuration"
        : "error";

    /// <summary>Known damage: drop the incremental receipt so the next handoff reads everything back, and queue an optional retry.</summary>
    private void Distrust(ReplicaAuditTarget target, string source, string digest)
    {
        new ReplicationVerification(config, state, source, target.Destination, digest, [], Now).Invalidate();
        if (!target.Required) StorageReplication.MarkPending(state, config.LibraryId, target.Name, Now);
    }

    private void Report(string message)
    {
        try { progress?.Invoke(message); }
        catch (Exception) { /* Progress output cannot interrupt a check. */ }
    }

    private void Record(ReplicaAuditAttempt attempt)
    {
        var history = Read(state, config.LibraryId);
        var targets = history.Targets.ToDictionary(t => t.Target, StringComparer.OrdinalIgnoreCase);
        var old = targets.GetValueOrDefault(attempt.Target) ?? new(attempt.Target, null, null, []);
        targets[attempt.Target] = new(attempt.Target,
            attempt.Complete ? attempt : old.LastComplete,
            attempt.Result == Passed ? attempt : old.LastPassed,
            old.Attempts.Prepend(attempt).Take(KeptAttempts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath(state))!);
        AtomicFile.Replace(HistoryPath(state), Json.Serialize(new ReplicaAuditHistory(ReplicaAuditHistory.CurrentFormat, config.LibraryId,
            targets.Values.OrderBy(t => t.Target, StringComparer.OrdinalIgnoreCase).ToArray())));
    }

    /// <summary>The saved history; empty when there is none or it belongs to another library.</summary>
    public static ReplicaAuditHistory Read(LocalState state, string libraryId)
    {
        var empty = new ReplicaAuditHistory(ReplicaAuditHistory.CurrentFormat, libraryId, []);
        if (!File.Exists(HistoryPath(state))) return empty;
        try
        {
            var history = Json.ReadFile<ReplicaAuditHistory>(HistoryPath(state), "Replica check history");
            return history.Format == ReplicaAuditHistory.CurrentFormat && history.LibraryId == libraryId && history.Targets is not null
                && history.Targets.All(t => t is not null && t.Target is not null && t.Attempts is not null) ? history : empty;
        }
        catch (InvalidDataException) { return empty; }
    }

    public static string Render(ReplicaAuditPlan plan, int? bandwidthLimitMiB, int timeoutMinutes)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Committed generation {plan.Generation}: {plan.Files} referenced file(s), {StorageVolume.Gb(plan.Bytes)}, compared from {plan.Source}.");
        foreach (var target in plan.Targets)
            text.AppendLine(CultureInfo.InvariantCulture, $"  {target.Name} ({(target.Required ? "required" : "optional")}): reads back {StorageVolume.Gb(plan.Bytes)} from {target.Destination}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Every byte is downloaded from each provider and compared. Expect provider bandwidth, API requests, time and possible download charges; "
            + $"{(bandwidthLimitMiB is { } limit ? $"the configured {limit} MiB/s limit applies" : "bandwidth is unlimited (replication.bandwidthLimitMiB)")}, "
            + $"and each target stops after {timeoutMinutes} minute(s).");
        text.Append("The check only reads: it never copies, replaces or deletes files, and does not take ownership.");
        return text.ToString();
    }

    public static string Render(ReplicaAuditAttempt attempt)
    {
        string reason = attempt.Reason is null ? "" : $" ({attempt.Reason})";
        return string.Create(CultureInfo.InvariantCulture,
            $"{attempt.FinishedAt:yyyy-MM-dd HH:mm} UTC  generation {attempt.Generation} ({attempt.SnapshotDigest[..12]})  {attempt.Result}{reason}"
            + $"  {attempt.Checked}/{attempt.Planned} checked: {attempt.Matched} matched, {attempt.Missing} missing, {attempt.Mismatched} different, {attempt.Unknown} unknown");
    }

    public static string Render(ReplicaAuditHistory history, IEnumerable<ReplicaTarget> configured, string? targetName)
    {
        var text = new StringBuilder();
        var names = configured.Select(t => t.Name).Concat(history.Targets.Select(t => t.Target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => targetName is null || n.Equals(targetName, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (string name in names)
        {
            var target = history.Targets.FirstOrDefault(t => t.Target.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (target is null || target.Attempts.Count == 0) { text.AppendLine(CultureInfo.InvariantCulture, $"{name}: never checked."); continue; }
            string lastComplete = target.LastComplete is { } complete
                ? string.Create(CultureInfo.InvariantCulture, $"{complete.Result} at {complete.FinishedAt:yyyy-MM-dd HH:mm} UTC (generation {complete.Generation})") : "none";
            string lastPass = target.LastPassed is { } passed
                ? string.Create(CultureInfo.InvariantCulture, $"{passed.FinishedAt:yyyy-MM-dd HH:mm} UTC (generation {passed.Generation}).") : "none.";
            text.AppendLine(CultureInfo.InvariantCulture, $"{name}: last complete check {lastComplete}; last pass {lastPass}");
            foreach (var attempt in target.Attempts) text.AppendLine("  " + Render(attempt));
        }
        if (names.Length == 0) text.AppendLine("No cloud copies are configured or checked.");
        return text.ToString().TrimEnd();
    }
}

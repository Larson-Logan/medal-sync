using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Util;

namespace MedalSync.Cli;

/// <summary><c>replica verify|history|prune|purge-trash</c>: inspect and tidy cloud copies while the library is Ready.</summary>
[SupportedOSPlatform("windows")]
internal static class ReplicaCommands
{
    public static int Verify(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        string? target = args.Value("--target");
        args.RequireAllUsed();
        var (state, config, store) = Open();
        using var operation = new ReadyOperation(state, config, apply, "Replica check", "the last complete result is kept",
            "Quit Medal before checking cloud copies.", "Configuration changed; preview the check again.");
        var audit = new ReplicaAudit(config, state, store, operation.Guard, new RcloneReplicaRemote(config, state, operation.Guard), output.WriteLine);
        try
        {
            var plan = audit.Plan(target);
            output.WriteLine(ReplicaAudit.Render(plan, config.Replication!.BandwidthLimitMiB, config.Replication.TimeoutMinutes));
            if (!apply) { output.WriteLine("Preview only. Run again with --apply to start the check; ownership stays Ready."); return 0; }
            var attempts = audit.Apply(plan);
            foreach (var attempt in attempts) output.WriteLine($"{attempt.Target}: {ReplicaAudit.Render(attempt)}");
            var damaged = attempts.Where(a => a.Missing + a.Mismatched > 0).Select(a => a.Target).ToArray();
            if (damaged.Length > 0)
                output.WriteLine($"Missing or different files at {string.Join(", ", damaged)}. The next handoff reads every file back there; "
                    + "optional copies are queued for 'medal-sync replicate retry'. Nothing was copied, replaced or deleted.");
            if (attempts.Any(a => a.Result == ReplicaAudit.Incomplete))
                output.WriteLine("Incomplete checks did not replace the last complete result. Fix the cause shown and check again.");
            return damaged.Length > 0 ? 1 : attempts.All(a => a.Result == ReplicaAudit.Passed) ? 0 : 3;
        }
        catch (OperationCanceledException ex)
        {
            output.WriteLine("Replica check canceled: " + ex.Message);
            return 3;
        }
    }

    public static int History(Arguments args, TextWriter output)
    {
        bool json = args.Flag("--json");
        string? target = args.Value("--target");
        args.RequireAllUsed();
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        var configured = config.Replication?.Targets ?? [];
        if (target is not null && !configured.Any(t => t.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
            && !ReplicaAudit.Read(state, config.LibraryId).Targets.Any(t => t.Target.Equals(target, StringComparison.OrdinalIgnoreCase)))
            throw new UsageException("--target must name a configured or previously checked cloud copy.");
        var history = ReplicaAudit.Read(state, config.LibraryId);
        if (json)
        {
            var shown = target is null ? history : history with { Targets = history.Targets.Where(t => t.Target.Equals(target, StringComparison.OrdinalIgnoreCase)).ToArray() };
            output.WriteLine(JsonSerializer.Serialize(shown, Json.Options));
        }
        else output.WriteLine(ReplicaAudit.Render(history, configured, target));
        return 0;
    }

    public static int Prune(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        string? input = args.Value("--in");
        string? destination = args.Value("--out");
        string? target = args.Value("--target");
        int grace = args.Value("--grace-days") is { } g ? Number("--grace-days", g, 1, 3650) : ReplicaPrune.DefaultGraceDays;
        int? history = args.Value("--history-older-than") is { } h ? Number("--history-older-than", h, 1, 3650) : null;
        bool previewOptions = args.Value("--grace-days") is not null || history is not null;
        args.RequireAllUsed();
        if (apply ? input is null || destination is not null || target is not null || previewOptions : destination is null || input is not null || target is null)
            throw new UsageException("Use replica prune --target NAME --out FILE to review, or replica prune --in FILE --apply to apply that exact review.");
        if (!apply && File.Exists(destination)) throw new UsageException($"{destination} already exists; choose a new file for the review.");
        var (state, config, store) = Open();
        using var operation = new ReadyOperation(state, config, apply, "Replica cleanup", "reviewed files already moved stay in the trash folder; apply the same plan again",
            "Quit Medal before cleaning up cloud copies.", "Configuration changed; preview the cleanup again.");
        var prune = new ReplicaPrune(config, state, store, operation.Guard, new RcloneReplicaRemote(config, state, operation.Guard));
        try
        {
            if (!apply)
            {
                var plan = prune.Plan(target!, grace, history);
                AtomicFile.CreateNew(destination!, Json.Serialize(plan));
                output.WriteLine(ReplicaPrune.Render(plan));
                output.WriteLine($"Review saved to {destination}. It lists private file names: keep it on this PC. Read it, then run 'medal-sync replica prune --in \"{destination}\" --apply'.");
                return 0;
            }
            var reviewed = Json.ReadFile<ReplicaPrunePlan>(input!, "Replica cleanup review");
            // No PC can start, finish or publish while the writer lock is held.
            using var writer = store.AcquireWriterLock(TimeSpan.FromSeconds(10));
            var result = prune.Apply(reviewed);
            output.WriteLine($"Moved {result.Moved} file(s) to {reviewed.Destination}/{result.TrashFolder}/" + (result.AlreadyMoved > 0 ? $" ({result.AlreadyMoved} were already there)" : "")
                + $"; {Storage.StorageVolume.Gb(result.Bytes)} in this batch. Nothing was deleted. When you are sure, run "
                + $"'medal-sync replica purge-trash --target {reviewed.Target}' to review deleting old trash.");
            return 0;
        }
        catch (OperationCanceledException ex)
        {
            output.WriteLine("Replica cleanup canceled: " + ex.Message);
            return 3;
        }
    }

    public static int PurgeTrash(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        string target = args.Required("--target");
        int days = args.Value("--older-than") is { } d ? Number("--older-than", d, 0, 3650) : ReplicaPrune.DefaultPurgeDays;
        args.RequireAllUsed();
        var (state, config, store) = Open();
        using var operation = new ReadyOperation(state, config, apply, "Trash purge", "remaining trash is kept",
            "Quit Medal before purging cloud trash.", "Configuration changed; preview the purge again.");
        var prune = new ReplicaPrune(config, state, store, operation.Guard, new RcloneReplicaRemote(config, state, operation.Guard));
        try
        {
            var all = prune.Trash(target);
            var due = prune.Purge(target, days, apply: false);
            foreach (var batch in all)
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {batch.Name}: {batch.Files} file(s), {Storage.StorageVolume.Gb(batch.Bytes)}{(due.Contains(batch) ? (apply ? "  deleting" : "  due") : "  kept")}"));
            if (all.Count == 0) { output.WriteLine("The trash is empty."); return 0; }
            if (!apply)
            {
                output.WriteLine($"{due.Count} batch(es) are older than {days} day(s). Preview only: add --apply to delete them permanently.");
                return 0;
            }
            // Hold the writer lock, as prune --apply does, so a prune resumed on another PC
            // cannot add files to a batch between choosing it and deleting it.
            using var writer = store.AcquireWriterLock(TimeSpan.FromSeconds(10));
            var deleted = prune.Purge(target, days, apply: true);
            output.WriteLine($"Deleted {deleted.Count} trash batch(es) permanently.");
            return 0;
        }
        catch (OperationCanceledException ex)
        {
            output.WriteLine("Trash purge canceled: " + ex.Message);
            return 3;
        }
    }

    private static (LocalState State, MedalSyncConfig Config, CoordinationStore Store) Open()
    {
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        if (config.Replication is not { Targets.Count: > 0 }) throw new UnsafeOperationException("No cloud copies are configured.");
        return (state, config, CoordinationStore.Open(config.LibraryCoordinationDirectory, config.LibraryId, config.AccountId));
    }

    internal static int Number(string name, string value, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= min && number <= max
            ? number : throw new UsageException($"{name} must be a whole number from {min} to {max}.");
}

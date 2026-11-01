using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Util;

namespace MedalSync.Cli;

/// <summary><c>retention</c>: review, then remove old generations and this PC's old recovery folders while Ready.</summary>
[SupportedOSPlatform("windows")]
internal static class RetentionCommands
{
    public static int Run(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        string? input = args.Value("--in");
        string? destination = args.Value("--out");
        var defaults = new RetentionPolicy();
        string? keep = args.Value("--keep"), keepDays = args.Value("--keep-days"), grace = args.Value("--grace-days");
        args.RequireAllUsed();
        bool policyGiven = keep is not null || keepDays is not null || grace is not null;
        if (apply ? input is null || destination is not null || policyGiven : destination is null || input is not null)
            throw new UsageException("Use retention --out FILE [--keep N] [--keep-days D] [--grace-days G] to review, or retention --in FILE --apply to apply that exact review.");
        if (!apply && File.Exists(destination)) throw new UsageException($"{destination} already exists; choose a new file for the review.");
        var policy = new RetentionPolicy(
            keep is null ? defaults.Keep : ReplicaCommands.Number("--keep", keep, 0, 10_000),
            keepDays is null ? defaults.KeepDays : ReplicaCommands.Number("--keep-days", keepDays, 0, 3650),
            grace is null ? defaults.GraceDays : ReplicaCommands.Number("--grace-days", grace, 1, 3650));
        // Holding the single-instance lock refuses a launch on this PC; the writer lock refuses one elsewhere during apply.
        using var instance = SessionCommands.SingleInstance();
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        var store = CoordinationStore.Open(config);
        var retention = new Retention(config, state, state.ExistingDevice(), store);
        if (!apply)
        {
            var plan = retention.Plan(policy);
            AtomicFile.CreateNew(destination!, Json.Serialize(plan));
            output.WriteLine(Retention.Render(plan));
            output.WriteLine($"Review saved to {destination}. Keep it on this PC. Apply it with 'medal-sync retention --in \"{destination}\" --apply' while every PC stays Ready.");
            return 0;
        }
        var reviewed = Json.ReadFile<RetentionPlan>(input!, "Retention review");
        string receipt = retention.Apply(reviewed, SetupMaintenance.RequireNotRunning);
        output.WriteLine($"Removed {reviewed.Items.Count} folder(s), {Storage.StorageVolume.Gb(reviewed.Items.Sum(i => i.Bytes))}. Receipt: {receipt}");
        return 0;
    }
}

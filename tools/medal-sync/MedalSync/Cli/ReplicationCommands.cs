using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;

namespace MedalSync.Cli;

[SupportedOSPlatform("windows")]
internal static class ReplicationCommands
{
    public static int Retry(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        string? target = args.Value("--target");
        args.RequireAllUsed();
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        var store = CoordinationStore.Open(config.LibraryCoordinationDirectory, config.LibraryId, config.AccountId);
        using var operation = new ReadyOperation(state, config, apply, "Cloud retry", "unfinished copies remain pending",
            "Quit Medal before retrying pending cloud copies.", "Configuration changed; preview the cloud retry again.");
        ReplicaRetryStatus? telemetry = null;
        try
        {
            var retry = new ReadyReplicaRetry(config, state, store, operation.Guard, progress: p => { output.WriteLine(p.NextAction); telemetry?.Report(p); });
            var plan = retry.Plan(target);
            output.WriteLine($"Committed generation {plan.Generation}: {plan.Targets.Count} pending cloud copy/copies{(plan.Targets.Count > 0 ? ": " + string.Join(", ", plan.Targets) : ".")}");
            if (!apply) { output.WriteLine("Preview only. Run again with --apply to copy and verify; ownership stays Ready."); return 0; }
            telemetry = new ReplicaRetryStatus(state, config.LibraryId, plan.Generation);
            telemetry.Start();
            var result = retry.Apply(plan, target);
            string message = result.Deferred.Count == 0 ? "Cloud retry finished; selected pending copies are verified."
                : $"Cloud retry finished; {string.Join(", ", result.Deferred)} remain pending. Check storage and retry.";
            message += result.Summary;
            telemetry.Complete(message);
            output.WriteLine(message);
            return result.Deferred.Count == 0 ? 0 : 3;
        }
        catch (OperationCanceledException ex)
        {
            string message = "Cloud retry canceled: " + ex.Message;
            telemetry?.Complete(message);
            output.WriteLine(message);
            return 3;
        }
        catch
        {
            telemetry?.Complete("Cloud retry stopped safely; unfinished copies remain pending. Check storage and retry while Ready.");
            throw;
        }
        finally
        {
            telemetry?.Dispose();
        }
    }
}

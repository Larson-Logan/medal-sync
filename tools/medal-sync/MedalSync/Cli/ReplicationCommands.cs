using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Util;

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
        string fingerprint = Hashing.Sha256File(state.ConfigPath);
        var store = CoordinationStore.Open(config.LibraryCoordinationDirectory, config.LibraryId, config.AccountId);
        using var interlock = apply ? ReplicaRetryInterlock.Retry() : null;
        var shutdown = new SessionShutdown(new SystemClock());
        using var ending = apply ? new WindowsSessionEnd(shutdown, _ => { }, setOrder: false) : null;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        if (apply) Console.CancelKeyPress += cancel;
        ReplicaRetryStatus? telemetry = null;
        try
        {
            void Guard()
            {
                cancellation.Token.ThrowIfCancellationRequested();
                SetupMaintenance.RequireNotRunning();
                interlock?.RequireContinuing();
                if (shutdown.Ending) throw new OperationCanceledException("Cloud retry stopped for Windows shutdown; unfinished copies remain pending.");
                if (SessionCommands.AnotherInstanceRunning() || Directory.Exists(config.DriveRoot.Display))
                    throw new OperationCanceledException("Cloud retry stopped for a local library operation; unfinished copies remain pending.");
                if (Hashing.Sha256File(state.ConfigPath) != fingerprint)
                    throw new OperationCanceledException("Configuration changed; preview the cloud retry again.");
                // No library database or new identity is needed; only observe whether Medal is running.
                foreach (string name in new[] { "Medal", "MedalEncoder", "MedalRecorder" })
                {
                    var processes = System.Diagnostics.Process.GetProcessesByName(name);
                    bool running = processes.Length != 0;
                    foreach (var process in processes) process.Dispose();
                    if (running) throw new OperationCanceledException("Quit Medal before retrying pending cloud copies.");
                }
            }
            var retry = new ReadyReplicaRetry(config, state, store, Guard, progress: p => { output.WriteLine(p.NextAction); telemetry?.Report(p); });
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
            if (apply) Console.CancelKeyPress -= cancel;
            shutdown.Complete();
        }
    }
}

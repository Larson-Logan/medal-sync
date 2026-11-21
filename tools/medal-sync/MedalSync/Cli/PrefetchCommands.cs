using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;

namespace MedalSync.Cli;

/// <summary>
/// <c>prefetch [--apply]</c>: downloads clips another PC published into this PC's persistent local copy while it is
/// idle and does not own the library. The status app starts it in the background; it can also be run by hand.
/// Exit 0 when nothing is left to download, 3 when it stopped early (activity, launch, space, an outage, a newer
/// generation) and 1 when it refused.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PrefetchCommands
{
    public static int Run(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        args.RequireAllUsed();
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        if (!MediaPrefetch.Enabled(config))
        {
            output.WriteLine(config.Replication?.LocalCopyRoot is null
                ? "Prefetch needs a persistent local copy (replication.localCopyRoot); nothing to do."
                : "Prefetch is turned off (replication.prefetchWhileIdle is false); nothing to do.");
            return 0;
        }
        var device = state.ExistingDevice();
        using var operation = new ReadyOperation(state, config, apply, "Prefetch", "clips already downloaded stay; launch downloads the rest",
            "Medal is open; prefetch waits until it is quit.", "Configuration changed; prefetch stopped.");
        var idle = new WindowsIdle();
        void Guard()
        {
            operation.Guard();
            // The same rules as idle sync: no input for a minute and a quiet CPU. Prefetch is never worth interrupting anyone.
            if (apply && !idle.IsIdle()) throw new OperationCanceledException("Prefetch paused: this PC is in use.");
        }
        try
        {
            if (apply)
            {
                // The CPU check needs two samples; the first one only sets a baseline.
                _ = idle.IsIdle();
                Thread.Sleep(TimeSpan.FromSeconds(2.2));
            }
            // In cloud mode the guard also stops journal and history requests, so a launch never waits on a stalled one.
            var store = CoordinationStore.Open(config, Guard);
            var prefetch = new MediaPrefetch(config, state, store, device, Guard);
            if (!apply)
            {
                var (generation, files) = prefetch.Plan();
                output.WriteLine($"Generation {generation}: {files.Count} clip file(s), {Storage.StorageVolume.Gb(files.Sum(f => f.Size))}, "
                    + "are missing from this PC's persistent local copy. Preview only: add --apply to download them while this PC is idle.");
                return 0;
            }
            var result = prefetch.Run();
            output.WriteLine($"Generation {result.Generation}: downloaded and verified {result.Downloaded} clip file(s), {Storage.StorageVolume.Gb(result.Bytes)}."
                + (result.Remaining == 0 ? " Nothing is left to download."
                    : $" {result.Remaining} file(s), {Storage.StorageVolume.Gb(result.RemainingBytes)}, remain ({Reason(result.Stopped)}); launch downloads them if needed."));
            return result.Remaining == 0 ? 0 : 3;
        }
        catch (OperationCanceledException ex)
        {
            output.WriteLine(ex.Message);
            return 3;
        }
        catch (IOException ex) when (ex is StorageOfflineException || ex is RcloneFailedException rclone && RcloneFailure.IsTransient(rclone))
        {
            output.WriteLine($"Prefetch is waiting for storage ({ex.Message}). Nothing partial was kept.");
            return 3;
        }
    }

    private static string Reason(string? stopped) => stopped switch
    {
        "space" => "the local copy's disk is at its free-space reserve",
        null => "nothing",
        _ => stopped,
    };
}

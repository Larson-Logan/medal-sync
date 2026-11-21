using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Util;

namespace MedalSync.Cli;

/// <summary>
/// <c>primary prune|purge-trash</c>: the reviewed cleanup of clips deleted in Medal from primary storage. It reuses the
/// cloud-copy cleanup with stricter reference rules (see <see cref="ReplicaPrune"/>). Apply holds the journal writer
/// lock, so no PC can start or finish meanwhile, and moves files into a trash folder; only purge-trash deletes.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PrimaryCommands
{
    public static int Prune(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        string? input = args.Value("--in");
        string? destination = args.Value("--out");
        int grace = args.Value("--grace-days") is { } g ? ReplicaCommands.Number("--grace-days", g, 1, 3650) : ReplicaPrune.DefaultGraceDays;
        bool previewOptions = args.Value("--grace-days") is not null;
        args.RequireAllUsed();
        if (apply ? input is null || destination is not null || previewOptions : destination is null || input is not null)
            throw new UsageException("Use primary prune --out FILE to review, or primary prune --in FILE --apply to apply that exact review.");
        if (!apply && File.Exists(destination)) throw new UsageException($"{destination} already exists; choose a new file for the review.");
        var (state, config, store) = Open();
        using var operation = new ReadyOperation(state, config, apply, "Primary cleanup", "reviewed files already moved stay in the trash folder; apply the same plan again",
            "Quit Medal before cleaning up primary storage.", "Configuration changed; preview the cleanup again.");
        var prune = Prune(config, state, store, operation.Guard);
        try
        {
            RequirePrimary(config, state, operation.Guard);
            if (!apply)
            {
                var plan = prune.Plan(ReplicaPrune.PrimaryTarget, grace);
                AtomicFile.CreateNew(destination!, Json.Serialize(plan));
                output.WriteLine(ReplicaPrune.Render(plan));
                if (config.Replication?.LocalCopyRoot is { } local)
                    output.WriteLine($"This PC's persistent local copy ({local}) is not cleaned by this command.");
                output.WriteLine($"Review saved to {destination}. It lists private file names: keep it on this PC. Read it, then run 'medal-sync primary prune --in \"{destination}\" --apply'.");
                return 0;
            }
            var reviewed = Json.ReadFile<ReplicaPrunePlan>(input!, "Primary cleanup review");
            // No PC can start, finish or publish while the writer lock is held.
            var result = ReplicaCommands.ApplyPrune(prune, store, reviewed);
            output.WriteLine($"Moved {result.Moved} file(s) to {reviewed.Destination}/{result.TrashFolder}/" + (result.AlreadyMoved > 0 ? $" ({result.AlreadyMoved} were already there)" : "")
                + $"; {Storage.StorageVolume.Gb(result.Bytes)} in this batch. Nothing was deleted. When you are sure, run "
                + "'medal-sync primary purge-trash' to review deleting old trash.");
            return 0;
        }
        catch (OperationCanceledException ex)
        {
            output.WriteLine("Primary cleanup canceled: " + ex.Message);
            return 3;
        }
        catch (IOException ex) when (Offline(ex))
        {
            output.WriteLine($"Waiting for storage: primary storage cannot be reached ({ex.Message}). Retry the same review. Any files already moved remain in its trash folder.");
            return 3;
        }
    }

    public static int PurgeTrash(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        int days = args.Value("--older-than") is { } d ? ReplicaCommands.Number("--older-than", d, 0, 3650) : ReplicaPrune.DefaultPurgeDays;
        args.RequireAllUsed();
        var (state, config, store) = Open();
        using var operation = new ReadyOperation(state, config, apply, "Trash purge", "remaining trash is kept",
            "Quit Medal before purging primary storage's trash.", "Configuration changed; preview the purge again.");
        var prune = Prune(config, state, store, operation.Guard);
        try
        {
            RequirePrimary(config, state, operation.Guard);
            output.WriteLine("Applying a nonempty purge makes this PC's next handoff read every referenced file on primary storage back.");
            // Hold the writer lock, as prune --apply does, so a prune resumed on another PC
            // cannot add files to a batch between choosing it and deleting it.
            return ReplicaCommands.PurgeTrash(prune, output, ReplicaPrune.PrimaryTarget, days, apply);
        }
        catch (OperationCanceledException ex)
        {
            output.WriteLine("Trash purge canceled: " + ex.Message);
            return 3;
        }
        catch (IOException ex) when (Offline(ex))
        {
            output.WriteLine($"Waiting for storage: primary storage cannot be reached ({ex.Message}). Retry the same purge when storage is back. Remaining trash is preserved.");
            return 3;
        }
    }

    internal static ReplicaPrune Prune(MedalSyncConfig config, LocalState state, CoordinationStore store, Action guard,
        IReplicaRemote? remote = null, Func<DateTimeOffset>? clock = null) =>
        new(config, state, store, guard, remote ?? (config.PrimaryIsRclone ? new RcloneReplicaRemote(config, state, guard) : new PrimaryFolderRemote(guard)),
            clock, primary: true);

    /// <summary>Proves primary storage is reachable and is this library's before anything is listed or moved.</summary>
    private static void RequirePrimary(MedalSyncConfig config, LocalState state, Action guard) =>
        PrimaryStorage.Checker(config, state, guard).RequireServer();

    private static bool Offline(IOException ex) => ex is StorageOfflineException
        || ex is RcloneFailedException rclone && RcloneFailure.IsTransient(rclone);

    private static (LocalState State, MedalSyncConfig Config, CoordinationStore Store) Open()
    {
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        return (state, config, CoordinationStore.Open(config));
    }
}

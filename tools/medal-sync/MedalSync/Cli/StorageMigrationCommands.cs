using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Cli;

[SupportedOSPlatform("windows")]
internal static class StorageMigrationCommands
{
    public static int Run(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply"), rollback = args.Flag("--rollback"), toCloud = args.Flag("--to-cloud");
        string? input = args.Value("--in"), destination = args.Value("--out"), remote = args.Value("--remote");
        string? journalId = args.Value("--cloud-journal-id"), credentials = args.Value("--rclone-config");
        string? coordination = args.Value("--coordination-root"), local = args.Value("--local-copy-root");
        args.RequireAllUsed();
        bool preview = !apply && !rollback;
        if (preview ? !toCloud || remote is null || journalId is null || destination is null || input is not null
            : apply == rollback || input is null || toCloud || remote is not null || journalId is not null
                || destination is not null || credentials is not null || coordination is not null || local is not null)
            throw new UsageException("Use storage migrate --to-cloud --remote NAME:FOLDER --cloud-journal-id ID --out FILE to review; then --in FILE --apply or --in FILE --rollback.");
        if (preview && File.Exists(destination)) throw new UsageException("The review file already exists. Choose a new output path.");

        using var instance = SessionCommands.SingleInstance();
        using var interlock = ReplicaRetryInterlock.Launch();
        var state = LocalState.ForCurrentUser();
        string fingerprint = Hashing.Sha256File(state.ConfigPath);
        var source = MedalSyncConfig.Load(state.ConfigPath);
        var device = state.ExistingDevice();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            Guard();
            var plan = preview ? null : Json.ReadFile<StorageMigrationPlan>(input!, "Storage migration review");
            if (!preview && apply && plan!.Target is { } completedTarget && source.StorageMode == StorageMode.Cloud
                && source.CloudJournalId == completedTarget.CloudJournalId && source.Remote == completedTarget.Remote
                && plan.DeviceId == device.DeviceId && plan.LibraryId == source.LibraryId
                && CloudCoordination.For(source, Guard).Read().Value.MigrationDigest == plan.Integrity)
            {
                output.WriteLine("Migration already completed and this PC uses the destination.");
                return 0;
            }
            if (source.StorageMode is not (StorageMode.Server or StorageMode.Cloud))
                throw new UnsafeOperationException("Migration to Google Drive cloud mode requires a server-mode or cloud-mode source library.");
            if (!preview) StorageMigration.Validate(plan!, source, device, fingerprint);
            var target = preview ? Target() : plan!.Target;
            var backend = CloudCoordination.For(target);
            var sourceStore = CoordinationStore.Open(source);
            var migration = new StorageMigration(source, state, device, sourceStore, target, backend, Guard);
            if (preview)
            {
                PrimaryStorage.Checker(source, state, Guard).RequireServer();
                var reviewed = migration.Preview(fingerprint);
                AtomicFile.CreateNew(destination!, Json.Serialize(reviewed));
                output.WriteLine($"Generation {reviewed.Head} is committed. The review includes {reviewed.Generations.Count} retained generations and "
                    + $"{reviewed.Media.Count} media paths ({reviewed.Media.Count(f => f.AlreadyOnDrive)} already verified on Drive).");
                output.WriteLine("Known PCs to reconfigure after migration:");
                foreach (var pc in reviewed.Devices.Where(pc => pc.DeviceId != device.DeviceId)) output.WriteLine($"  {pc.DisplayName}");
                output.WriteLine($"Private review saved to '{destination}'. With every PC Ready, apply it with 'medal-sync storage migrate --in \"{destination}\" --apply'.");
                return 0;
            }

            migration.Apply(plan!, rollback);
            if (rollback)
            {
                output.WriteLine("The original library remains authoritative. Any staged Drive files were preserved. Allocate a fresh journal ID before another migration.");
                return 0;
            }

            Guard();
            var proposal = ConfigurationReview.Prepare(state, target, false, (_, _) => Guard(), _ => Guard());
            StorageIdentity.Initialize(target.MountedCapture.Display, target.LibraryId);
            string? backup = proposal.Save(true, (_, _) => Guard(), _ => Guard());
            output.WriteLine($"Generation {plan!.Head} migrated to Google Drive. This PC is configured for the destination. Previous configuration: {backup}");
            output.WriteLine($"On each other PC, keep its device and library IDs and use cloud mode with '{target.Remote}' and journal ID '{target.CloudJournalId}'.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Migration paused. Apply the same review again, or roll back if the original journal has not been retired.");
            return 3;
        }
        finally { Console.CancelKeyPress -= cancel; }

        void Guard()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            SetupMaintenance.RequireNotRunning();
            if (Hashing.Sha256File(state.ConfigPath) != fingerprint || state.ExistingDevice().DeviceId != device.DeviceId)
                throw new UnsafeOperationException("The configuration or device identity changed. Restore the reviewed setup before retrying.");
            string sessions = Path.Combine(state.Root, "sessions");
            if (Directory.Exists(source.DriveRoot.Display) || Directory.Exists(sessions) && Directory.EnumerateFiles(sessions, "*.json").Any())
                throw new UnsafeOperationException("Finish the local session and stop the clip drive before migrating.");
            foreach (string name in new[] { "Medal", "MedalEncoder", "MedalRecorder" })
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(name);
                foreach (var process in processes) process.Dispose();
                if (processes.Length != 0) throw new UnsafeOperationException("Quit Medal before migrating the library.");
            }
        }

        MedalSyncConfig Target()
        {
            if (!ReplicationConfig.ValidRemote(remote!) || !MedalSyncConfig.ValidCloudJournalId(journalId) || journalId!.Contains('.'))
                throw new UsageException("Choose a named Google Drive remote and an unused journal ID from 'storage cloud-id'.");
            string file = credentials ?? source.Replication?.RcloneConfigPath ?? ProviderSignIn.DefaultConfigPath(state);
            if (source.PrimaryIsRclone && !Path.GetFullPath(Environment.ExpandEnvironmentVariables(file)).Equals(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(source.Replication!.RcloneConfigPath!)), StringComparison.OrdinalIgnoreCase))
                throw new UsageException("Put both rclone remotes in the source's configured credential file for this migration.");
            string suffix = Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(journalId))[..12];
            var config = source with
            {
                StorageMode = StorageMode.Cloud,
                Remote = remote!,
                CloudJournalId = journalId,
                CoordinationRoot = coordination ?? Path.Combine(state.Root, "coordination-" + suffix),
                Replication = (source.Replication ?? new ReplicationConfig(null, null, [])) with
                {
                    LocalCopyRoot = local ?? source.Replication?.LocalCopyRoot ?? Path.Combine(state.Root, "persistent-media"),
                    RcloneConfigPath = file,
                },
            };
            config.Validate();
            if (string.Equals(config.CoordinationRoot, source.CoordinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new UsageException("Choose a fresh local coordination cache for the destination.");
            return config;
        }
    }
}

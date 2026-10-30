using System.Data.Common;
using MedalSync.Library;
using MedalSync.Storage;

namespace MedalSync.Cli;

/// <summary>
/// Entry point. Exit codes: 0 done, 1 refused or blocked (nothing unsafe was
/// done), 2 usage error, 3 waiting (try again later).
/// </summary>
internal static class CommandRunner
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--json", "--apply", "--accept-library-changes", "--accept-unknown-media", "--replace-local-library", "--help", "--new-library", "--spike" };
    private static readonly HashSet<string> Valued = new(StringComparer.Ordinal)
    {
        "--out", "--in", "--db", "--checkpoint", "--session", "--reason", "--hours", "--free-gb", "--bitrate", "--reserve-gb", "--config", "--target",
        "--from", "--to", "--limit", "--before", "--wait-for-server", "--state-root", "--medal-link",
        "--local-copy-root", "--cloud-remote", "--rclone-config",
        "--storage-mode",
        "--replica-conflicts",
        "--grace-days", "--history-older-than", "--older-than", "--keep", "--keep-days",
        "--account-id", "--cache-root", "--remote", "--coordination-root", "--library-id", "--capture-folder", "--rclone-path", "--sync-folder", "--sync-pattern", "--spike-config", "--server-ignore",
    };

    public const string Help = """
        medal-sync: SSD-cached Medal storage and one-PC-at-a-time library handoff.

        Read-only:
          setup check [--state-root DIR] [--out FILE]  Verify readiness for Windows setup; no library changes
          config validate --config FILE          Validate a candidate config without reading Medal or the share
          inspect [--json]                       Phase 0 inventory of this PC (Gate A evidence)
          compatibility [--json]                 Check installed Medal builds and schema without configuration
          diagnostics [--out FILE]               Preview a privacy-safe summary for support; --out saves it to a new file
          snapshot export --out FILE [--db DB]   Write the library tables to a new private file
          snapshot verify --in FILE              Check a snapshot file and its schema
          dry-run-restore [--in FILE] [--db DB]  Restore into a disposable copy and verify it
          audit --checkpoint FILE [--db DB]      Find clips Medal dropped since a checkpoint
          capacity --bitrate MBPS --hours H --free-gb GB [--reserve-gb GB]
          mount-plan                             Show the reviewed rclone command (no secrets)
          storage check [--json]                 Check each rclone remote's sign-in and folder; never writes there
          status [--json]                        Who holds the shared library; --json is documented for scripts
          history list [--json] [--limit N] [--before N]  Committed generations, newest first
          history diff --from N --to M [--json] [--out FILE]  Count changes between generations;
                                                 --out writes per-clip titles and paths to a new private file
          replica history [--target NAME] [--json]  Past cloud-copy checks; counts only
          enroll --out DIRECTORY [--apply]       Preview/review a lossless union with this PC, then enroll it
          enroll recover --out DIRECTORY [--apply]  Recover an exact native-backup rollback after enrollment

        Changes state (need the configuration from the runbook):
          config create --account-id ID --cache-root DIR --remote PATH --out FILE
                        (--new-library | --library-id ID) [--spike] [--apply]
                        [--capture-folder M:\Clips] [--rclone-path FILE] [--sync-folder ID] [--sync-pattern PATTERN]
                        [--local-copy-root DIR] [--cloud-remote NAME:FOLDER --rclone-config FILE]
                        [--storage-mode server|local]  Local mode keeps media/journal on this PC; no SMB needed
                        [--coordination-root PATH]  Required UNC share in server mode; optional local folder in local mode
                        [--replica-conflicts stop|version]  Preserve earlier destination files when updating
                                                 Preview/create a config; never overwrite an existing file
          init [--apply]                         Create the shared library from this PC (generation 0)
          storage init-primary [--apply]         Mark an empty primary folder or rclone remote for this library
          replicate retry [--target NAME] [--apply]  Preview/retry pending optional cloud copies while Ready
          replica verify [--target NAME] [--apply]  Preview/read back cloud copies while Ready; never copies or deletes
          replica prune --target NAME --out FILE [--grace-days N] [--history-older-than D]
                                                 Review unreferenced files at an optional cloud copy
          replica prune --in FILE --apply        Move exactly those files to the copy's trash folder
          replica purge-trash --target NAME [--older-than D] [--apply]  Permanently delete old cloud trash
          retention --out FILE [--keep N] [--keep-days D] [--grace-days G]
                                                 Review old generations and recovery folders to remove
          retention --in FILE --apply            Remove exactly what that review listed (every PC Ready)
          launch [--replace-local-library] [--wait-for-server SECONDS] [--medal-link URL]
                                                 Start Medal through medal-sync and hand off on exit
          finish [--accept-library-changes]      Finish this PC's interrupted session
          recover-start --session ID [--apply]  Review/recover an older orphan login only when its cache is empty
          refresh-media --out FILE               Review conflicts in persistent local media (Medal quit)
          refresh-media --in FILE --apply [--accept-unknown-media]  Apply the exact review, retaining previous bytes
          repair --checkpoint FILE [--apply] [--db DB]   Put back clips Medal dropped (Medal quit)
          relocate --from DIR [--config FILE] [--apply]  Move library files left outside the clip folder (Medal quit)
          promote --spike-config FILE --config FILE --server-ignore FILE [--apply]
                                                 Verify/rename stopped spike storage to production
          abandon --session ID --reason TEXT [--apply]   Give up a lost PC's session explicitly
          spike start|status|stop --config FILE  Gate B mount experiment on a disposable '*spike*' remote

        See docs/getting-started.md and docs/storage.md before changing a live library.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error) => Run(args, output, error, null);

    /// <summary>As <see cref="Run(string[], TextWriter, TextWriter)"/>; <paramref name="notify"/> receives a running session's messages.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error, Action<string>? notify,
        Func<LibraryChangeReview, bool>? reviewLibraryChanges = null, Session.SessionShutdown? shutdown = null)
    {
        try
        {
            var parsed = Arguments.Parse(args, Flags, Valued);
            if (parsed.Flag("--help") || parsed.Words.Count == 0)
            {
                output.WriteLine(Help);
                return parsed.Words.Count == 0 && !args.Contains("--help") ? 2 : 0;
            }
            return Dispatch(parsed, output, notify, reviewLibraryChanges, shutdown);
        }
        catch (UsageException ex)
        {
            error.WriteLine($"medal-sync: {ex.Message} Run 'medal-sync --help'.");
            return 2;
        }
        catch (MedalSyncException ex)
        {
            error.WriteLine($"medal-sync: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DbException)
        {
            error.WriteLine($"medal-sync: stopped safely: {ex.Message}");
            return 1;
        }
    }

    private static int Dispatch(Arguments args, TextWriter output, Action<string>? notify,
        Func<LibraryChangeReview, bool>? reviewLibraryChanges, Session.SessionShutdown? shutdown)
    {
        string command = string.Join(' ', args.Words);
        // Read-only reports stay available while setup runs, which is often when help is needed.
        if (command is not ("version" or "setup check" or "diagnostics")) Platform.SetupMaintenance.RequireNotRunning();
        switch (command)
        {
            case "version":
                args.RequireAllUsed();
                output.WriteLine($"medal-sync {ToolInfo.Version}; supported Medal schema {string.Join(", ", MedalSchema.SupportedFingerprints)}");
                return 0;
            case "snapshot export": return LibraryCommands.Export(args, output);
            case "snapshot verify": return LibraryCommands.Verify(args, output);
            case "dry-run-restore": return LibraryCommands.DryRunRestore(args, output);
            case "audit": return LibraryCommands.Audit(args, output);
            case "repair": return LibraryCommands.Repair(args, output);
            case "capacity": return Capacity(args, output);
            case "config create": return ConfigCommands.Create(args, output);
            case "config validate": return ConfigCommands.Validate(args, output);
        }
        if (!OperatingSystem.IsWindows()) throw new UsageException($"'{command}' runs only on Windows.");
        return command switch
        {
            "setup check" => SetupCommands.Check(args, output),
            "inspect" => SessionCommands.Inspect(args, output),
            "compatibility" => SessionCommands.Compatibility(args, output),
            "diagnostics" => SessionCommands.Diagnostics(args, output),
            "status" => SessionCommands.Status(args, output),
            "history list" => HistoryCommands.List(args, output),
            "history diff" => HistoryCommands.Diff(args, output),
            "mount-plan" => SessionCommands.MountPlanCommand(args, output),
            "init" => SessionCommands.Init(args, output),
            "storage init-primary" => SessionCommands.InitPrimary(args, output),
            "storage check" => SessionCommands.CheckStorage(args, output),
            "replicate retry" => ReplicationCommands.Retry(args, output),
            "replica verify" => ReplicaCommands.Verify(args, output),
            "replica history" => ReplicaCommands.History(args, output),
            "replica prune" => ReplicaCommands.Prune(args, output),
            "replica purge-trash" => ReplicaCommands.PurgeTrash(args, output),
            "retention" => RetentionCommands.Run(args, output),
            "enroll" => EnrollmentCommands.Run(args, output),
            "enroll recover" => EnrollmentCommands.Recover(args, output),
            "launch" => SessionCommands.Launch(args, output, notify, reviewLibraryChanges, shutdown),
            "finish" => SessionCommands.Finish(args, output),
            "recover-start" => SessionCommands.RecoverStart(args, output),
            "refresh-media" => MediaRefreshCommands.Run(args, output),
            "abandon" => SessionCommands.Abandon(args, output),
            "relocate" => LibraryCommands.Relocate(args, output),
            "promote" => PromotionCommands.Run(args, output),
            "spike start" or "spike status" or "spike stop" => SessionCommands.Spike(args.Words[1], args, output),
            _ => throw new UsageException($"Unknown command '{command}'."),
        };
    }

    private static int Capacity(Arguments args, TextWriter output)
    {
        // Bounds keep every conversion and byte count in range: hours match the config's maxSessionHours,
        // and gigabyte values stay well inside a long once converted to bytes.
        double Number(string name, double min, bool minAllowed, double max)
        {
            if (!double.TryParse(args.Required(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
                || !double.IsFinite(v))
            {
                throw new UsageException($"{name} must be a finite number.");
            }
            if (v < min || (v == min && !minAllowed) || v > max)
            {
                throw new UsageException($"{name} must be {(minAllowed ? "at least" : "more than")} {min} and at most {max}.");
            }
            return v;
        }
        double bitrate = Number("--bitrate", 0, minAllowed: false, max: 10_000);
        double hours = Number("--hours", 0, minAllowed: false, max: 24);
        double free = Number("--free-gb", 0, minAllowed: true, max: 1_000_000);
        double reserve = args.Value("--reserve-gb") is null ? 20 * 1.073741824 : Number("--reserve-gb", 0, minAllowed: true, max: 1_000_000);
        args.RequireAllUsed();
        var session = TimeSpan.FromHours(hours);
        if (session <= TimeSpan.Zero) throw new UsageException("--hours is too small to measure.");
        var verdict = CapacityAdmission.Evaluate(new CapacityInputs((long)(free * 1e9), (long)(reserve * 1e9), bitrate, session, 0));
        output.WriteLine(verdict.Summary);
        return verdict.Admitted ? 0 : 1;
    }
}

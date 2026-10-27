using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Cli;

[SupportedOSPlatform("windows")]
internal static class PromotionCommands
{
    public static int Run(Arguments args, TextWriter output)
    {
        var spike = MedalSyncConfig.Load(args.Required("--spike-config"));
        var production = MedalSyncConfig.Load(args.Required("--config"));
        string serverIgnore = args.Required("--server-ignore");
        bool apply = args.Flag("--apply");
        args.RequireAllUsed();
        if (!spike.RemoteRoot.Segments.LastOrDefault("").Contains("spike", StringComparison.OrdinalIgnoreCase)
            || !spike.ServerCapture.Root.Equals(production.ServerCapture.Root, StringComparison.OrdinalIgnoreCase)
            || !spike.Capture.Equals(production.Capture) || spike.AccountId != production.AccountId || spike.LibraryId == production.LibraryId)
            throw new UnsafeOperationException("Promotion requires a spike source and a distinct production identity on the same UNC share, with the same account and capture folder.");
        var install = MedalEnvironment.Locate();
        if (install.AccountId != production.AccountId || new MedalProcess(install).IsRunning())
            throw new UnsafeOperationException("Quit Medal from its tray and use the matching signed-in account first.");
        if (Directory.Exists(spike.DriveRoot.Display)) throw new UnsafeOperationException("Wait for spike status to say Drained, then stop the spike mount before promotion.");
        string receiptDirectory = Path.Combine(LocalState.ForCurrentUser().BackupDirectory, "production-move-" + production.LibraryId);
        bool completed = File.Exists(Path.Combine(receiptDirectory, "receipt.json"));
        if (!completed && Directory.Exists(production.LibraryCoordinationDirectory))
            throw new UnsafeOperationException("Production coordination already exists; never move storage into an initialized library.");
        var files = StoragePromotion.Inspect(spike.ServerCapture.Display, production.ServerCapture.Display, receiptDirectory, spike.LibraryId, production.LibraryId);
        if (files.Count == 0 && completed)
        {
            output.WriteLine($"Already promoted; nothing changed. Receipt: {receiptDirectory}");
            return 0;
        }
        // Both configured exclusions and the server Media ignore must be set and
        // tested before this command. The command cannot prove remote peers' state.
        var exclusions = SyncthingCheck.Check(SyncthingCheck.Folders(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LarsonServer")), production.SyncExclusions);
        if (exclusions.Any(e => !e.Present)) throw new UnsafeOperationException("Local Syncthing exclusion is missing. Follow the runbook before promotion.");
        var ignorePath = WindowsPath.Parse(serverIgnore);
        if (!ignorePath.Root.Equals(production.ServerCapture.Root, StringComparison.OrdinalIgnoreCase)
            || ignorePath.Segments.LastOrDefault() != ".stignore" || production.SyncExclusions.Count != 1
            || File.ReadLines(ignorePath.Display).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)) != production.SyncExclusions[0].Pattern)
            throw new UnsafeOperationException("The server .stignore on the same share must start with the configured clip exclusion. Apply and test both peers' exclusions first.");
        output.WriteLine($"Move {files.Count} files: {spike.ServerCapture.Display} -> {production.ServerCapture.Display}.");
        if (!apply)
        {
            output.WriteLine("Preview only. --apply hashes every file, saves a library checkpoint, preserves the empty destination and spike marker, renames, and verifies all hashes. Nothing is deleted.");
            return 0;
        }
        var result = StoragePromotion.Apply(spike.ServerCapture.Display, production.ServerCapture.Display, receiptDirectory, spike.LibraryId, production.LibraryId,
            path =>
            {
                if (new MedalProcess(install).IsRunning() || Directory.Exists(spike.DriveRoot.Display))
                    throw new UnsafeOperationException("Medal or the mount reopened during hashing; nothing was moved.");
                AtomicFile.CreateNew(path, LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId).ToCanonicalJson());
            });
        output.WriteLine($"Promoted and verified {result.Files.Count} files ({result.Files.Sum(f => f.Bytes)} bytes). Receipt: {receiptDirectory}");
        output.WriteLine("Next: relocate any remaining references, preview/init generation 0, then rewire startup. Spike caches and all originals remain retained.");
        return 0;
    }
}

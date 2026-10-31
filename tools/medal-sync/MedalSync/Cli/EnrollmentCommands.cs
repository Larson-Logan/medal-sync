using System.Runtime.Versioning;
using System.Text.Json;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Util;

namespace MedalSync.Cli;

internal sealed record EnrollmentReview(int Format, string LibraryId, string DeviceId, long Generation, string SharedDigest,
    string LocalDigest, string CandidateDigest, string Source, string Destination, int Added, int Matched,
    IReadOnlyList<IdentityMapping> Identities, IReadOnlyList<EnrollmentFile> Files, IReadOnlyList<string> Conflicts);

/// <summary>Enrollment never uses a blanket replacement of an unreconciled library.</summary>
[SupportedOSPlatform("windows")]
internal static class EnrollmentCommands
{
    public static int Recover(Arguments args, TextWriter output)
    {
        string directory = Path.GetFullPath(args.Required("--out"));
        bool apply = args.Flag("--apply");
        args.RequireAllUsed();
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        if (config.PrimaryIsRclone) throw new UnsafeOperationException("Enrollment requires an SMB primary; cloud primary copies are not supported by this command.");
        var install = MedalEnvironment.Locate();
        var device = state.Device();
        if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Quit Medal from its tray before recovering enrollment.");
        var review = Json.ReadFile<EnrollmentReview>(Path.Combine(directory, "review.json"), "Enrollment review");
        if (review.LibraryId != config.LibraryId || review.DeviceId != device.DeviceId || install.AccountId != config.AccountId)
            throw new UnsafeOperationException("Enrollment recovery belongs to another device, library or account.");
        var candidate = LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(Path.Combine(directory, "candidate.json")));
        var store = CoordinationStore.Open(config.LibraryCoordinationDirectory, config.LibraryId, config.AccountId);
        var journal = store.ReadJournal();
        if (candidate.Digest() != review.CandidateDigest || store.Generations.Read(journal.CommittedGeneration).Snapshot.Digest() != review.CandidateDigest
            || journal.Owner?.DeviceId != device.DeviceId || journal.State != OwnershipState.Draining)
            throw new UnsafeOperationException("Recovery requires this PC's blocked draining session and the committed enrollment candidate.");
        var current = LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId);
        if (current.Digest() != review.SharedDigest)
            throw new UnsafeOperationException("The current library is not the exact pre-enrollment shared backup. Preserve/reconcile its new work before recovery.");
        string? original = DatabaseBackup.List(directory, config.AccountId).FirstOrDefault(path =>
            LibraryDatabase.ReadSnapshot(path, config.AccountId).Digest() == review.LocalDigest);
        if (original is null) throw new UnsafeOperationException("No verified original database for this PC was retained in the review directory.");
        output.WriteLine("The exact pre-enrollment shared library replaced this PC's union. Recovery will restore this PC's own saved device settings and the committed union; no media is removed.");
        if (!apply) { output.WriteLine("Preview only. Add --apply with Medal quit to recover this verified case."); return 0; }
        using var mutex = new Mutex(false, SessionCommands.InstanceMutex);
        bool locked;
        try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
        if (!locked) throw new UnsafeOperationException("Another medal-sync operation is running.");
        try
        {
            // Recheck after acquiring the local guard; keep a forensic copy of
            // what Medal restored before replacing any settings or library rows.
            if (new MedalProcess(install).IsRunning() || LibraryDatabase.ReadSnapshot(install.DatabasePath, config.AccountId).Digest() != current.Digest())
                throw new UnsafeOperationException("Medal or the local library changed before recovery.");
            var latest = store.ReadJournal();
            if (latest != journal) throw new UnsafeOperationException("Ownership changed before recovery.");
            string evidence = Path.Combine(directory, "native-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidence);
            AtomicFile.CreateNew(Path.Combine(evidence, "before.json"), current.ToCanonicalJson());
            DeviceSettingsRestorer.Restore(install.DatabasePath, original, config.AccountId, evidence);
            LibraryRestorer.Restore(install.DatabasePath, candidate, new RestoreOptions(config.AccountId, evidence), DateTimeOffset.UtcNow);
            CaptureFolderSetting.Change(install.DatabasePath, review.Source, config.Capture.Display);
            NativeBackupCompatibility.Prepare(config, install, state);
            AtomicFile.CreateNew(Path.Combine(evidence, "recovered.json"), candidate.ToCanonicalJson());
            output.WriteLine($"Recovered the union and original device settings. Evidence: {evidence}. Run finish before opening Medal again.");
            return 0;
        }
        finally { mutex.ReleaseMutex(); }
    }

    public static int Run(Arguments args, TextWriter output)
    {
        string directory = Path.GetFullPath(args.Required("--out"));
        bool apply = args.Flag("--apply");
        args.RequireAllUsed();
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        if (config.PrimaryIsRclone) throw new UnsafeOperationException("Enrollment requires an SMB primary; cloud primary copies are not supported by this command.");
        var install = MedalEnvironment.Locate();
        var device = state.Device();
        if (install.AccountId != config.AccountId) throw new IncompatibleLibraryException("Enrollment account differs from this PC's signed-in Medal account.");
        using var mutex = new Mutex(false, SessionCommands.InstanceMutex);
        bool locked;
        try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
        if (!locked) throw new UnsafeOperationException("Another medal-sync operation is running on this PC.");
        try
        {
            if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Quit Medal from its tray before preparing or applying enrollment.");
            var store = CoordinationStore.Open(config.LibraryCoordinationDirectory, config.LibraryId, config.AccountId);
            var journal = store.ReadJournal();
            if (journal.State != OwnershipState.Clean) throw new UnsafeOperationException("Both PCs must be Ready before enrollment. The existing owner must finish first.");
            var shared = store.Generations.Read(journal.CommittedGeneration);
            NativeBackupBuilds.RequireCompatible(shared.Manifest.Medal, install.Versions);
            new MedalCompatibilityGuard(install).RequireLaunch();
            var local = LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId);
            string sourceText;
            using (var connection = LibraryDatabase.OpenReadOnly(install.DatabasePath))
                sourceText = JsonSerializer.Deserialize<string>(LibraryDatabase.ReadSetting(connection, null, "ClipFolder") ?? "null")
                    ?? throw new UnsafeOperationException("Medal has no capture folder.");
            var source = WindowsPath.Parse(sourceText);
            if (source.Root.StartsWith(@"\\", StringComparison.Ordinal) || source.Root == config.DriveRoot.Root)
                throw new UnsafeOperationException("Enrollment expects this PC's original local capture folder, before starting the shared mount.");
            var destination = config.Capture.Append("Devices", device.DeviceId);
            var plan = LibraryReconciliation.Plan(shared.Snapshot, local, source, destination);
            var candidate = CountRebuilder.Rebuild(plan.Snapshot, install.DatabasePath);
            var review = new EnrollmentReview(1, config.LibraryId, device.DeviceId, journal.CommittedGeneration,
                shared.Snapshot.Digest(), local.Digest(), candidate.Digest(), source.Display, destination.Display,
                plan.Added, plan.Matched, plan.Identities, plan.Files, plan.Conflicts);
            output.WriteLine($"Union: {candidate.RowCount("contents")} entries; {plan.Added} unique entries added; {plan.Matched} shared identities matched; {plan.Conflicts.Count} conflicts.");
            foreach (string conflict in plan.Conflicts.Take(20)) output.WriteLine("  " + conflict);
            if (!apply)
            {
                if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                    throw new UnsafeOperationException("The review directory is not empty. Keep that evidence and choose a new private directory.");
                Directory.CreateDirectory(directory);
                AtomicFile.CreateNew(Path.Combine(directory, "review.json"), Json.Serialize(review));
                AtomicFile.CreateNew(Path.Combine(directory, "local-before.json"), local.ToCanonicalJson());
                AtomicFile.CreateNew(Path.Combine(directory, "shared-before.json"), shared.Snapshot.ToCanonicalJson());
                AtomicFile.CreateNew(Path.Combine(directory, "candidate.json"), candidate.ToCanonicalJson());
                output.WriteLine($"Private review saved in {directory}. No media, library, startup or journal was changed.");
                output.WriteLine("Review every conflict before enrollment. Original local media is retained; files receive a device-specific destination.");
                return plan.Conflicts.Count == 0 ? 0 : 1;
            }
            var approved = Json.ReadFile<EnrollmentReview>(Path.Combine(directory, "review.json"), "Enrollment review");
            if (Hashing.Sha256Hex(Json.Serialize(approved)) != Hashing.Sha256Hex(Json.Serialize(review)))
                throw new UnsafeOperationException("The library or enrollment plan changed since the preview. Prepare a fresh review; nothing was changed.");
            if (plan.Conflicts.Count != 0) throw new UnsafeOperationException("Enrollment has unresolved conflicts; neither library was changed.");
            return Apply(config, install, state, store, device, review, candidate, directory, output);
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal static int Apply(MedalSyncConfig config, MedalInstall install, LocalState state, CoordinationStore store,
        DeviceIdentity device, EnrollmentReview review, LibrarySnapshot candidate, string directory, TextWriter output)
    {
        // The receipt is written before acquiring ownership. An interrupted apply
        // is never replayed blindly; all copies and the original DB remain.
        string receipt = Path.Combine(directory, "apply.json");
        if (File.Exists(receipt)) throw new UnsafeOperationException("This enrollment was already attempted. Inspect apply.json and recover it before another apply.");
        if (Directory.Exists(config.DriveRoot.Display)) throw new UnsafeOperationException("The virtual drive is occupied. Stop its clean session first.");
        var checker = new ShareAssetChecker(config.Capture, config.ServerCapture, config.LibraryId, new FileSystemProbe());
        checker.RequireServer();
        var originalFiles = AssetInventory.Check(AssetInventory.Collect(LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId)),
            [WindowsPath.Parse(review.Source)], new FileSystemProbe());
        if (originalFiles.Blocking.Any()) throw new UnsafeOperationException("An original local asset is missing or unsafe; enrollment did not begin.");
        var optionalMissing = originalFiles.Items.Where(a => a.Reference.Role == AssetRole.Optional && a.Problem == AssetProblem.Missing)
            .Select(a => a.Reference.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copies = review.Files.Where(f => !optionalMissing.Contains(f.Source)).ToArray();
        long bytes = copies.Sum(f => new FileInfo(f.Source).Length);
        var space = Inventory.FreeSpace(config.ServerCapture.Display);
        if (space is null || space.Value.Free < bytes + (20L << 30)) throw new UnsafeOperationException("Cannot verify server space for enrollment plus a 20 GiB reserve.");
        string session = Guid.NewGuid().ToString("N");
        void Save(string stage, long? generation = null) => AtomicFile.Replace(receipt,
            Json.Serialize(new { format = 1, stage, session, review.LocalDigest, review.SharedDigest, review.CandidateDigest,
                source = review.Source, destination = review.Destination, generation, at = DateTimeOffset.UtcNow }));
        using (var db = LibraryDatabase.OpenReadOnly(install.DatabasePath)) DatabaseBackup.Create(db, directory, install.AccountId, DateTimeOffset.UtcNow);
        Save("prepared");
        bool acquired = false;
        try
        {
            store.Update(r => r.CommittedGeneration == review.Generation
                ? Ownership.Acquire(r, device, session, DateTimeOffset.UtcNow)
                : throw new UnsafeOperationException("Another PC published since the review. Prepare a fresh preview."), TimeSpan.FromSeconds(5));
            acquired = true;
            Save("copying");
            int done = 0;
            foreach (var file in copies)
            {
                if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Medal reopened during enrollment. Quit it before recovery.");
                string target = WindowsPath.Parse(file.CapturePath).Rebase(config.Capture, config.ServerCapture).Display;
                string hash = EnrollmentCopy.Copy(file.Source, target);
                File.AppendAllText(Path.Combine(directory, "copies.jsonl"), JsonSerializer.Serialize(new { file.Source, target, sha256 = hash }) + "\n");
                done++;
                if (done % 25 == 0 || done == copies.Length) output.WriteLine($"Verified {done}/{copies.Length} files ({bytes / 1e9:0.0} GB total).");
            }
            Save("copied");
            if (checker.OnServer(candidate).Blocking.Any()) throw new UnsafeOperationException("The merged library has unavailable server assets. Originals and copies are retained.");
            if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Medal reopened before library restore.");
            if (LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId).Digest() != review.LocalDigest)
                throw new UnsafeOperationException("The local library changed during enrollment; the original and copied files are retained.");
            var result = LibraryRestorer.Restore(install.DatabasePath, candidate, new RestoreOptions(install.AccountId, directory), DateTimeOffset.UtcNow);
            Save("restored");
            CaptureFolderSetting.Change(install.DatabasePath, review.Source, config.Capture.Display);
            Save("capture-configured");
            if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Medal reopened before publication.");
            if (LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId).Digest() != candidate.Digest())
                throw new UnsafeOperationException("The merged library changed before publication.");
            store.Update(r => Ownership.MarkActive(r, device, session, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5));
            store.Update(r => Ownership.BeginDrain(r, device, session, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5));
            store.Update(r => Ownership.BeginPublish(r, device, session, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5));
            long next = store.Generations.Next(review.Generation);
            store.Generations.Publish(next, review.Generation, session, device, candidate, AssetManifest.From(checker.OnServer(candidate)), install.Versions, DateTimeOffset.UtcNow);
            store.Update(r => Ownership.CompletePublish(r, device, session, next, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5));
            Save("completed", next);
            output.WriteLine($"Enrolled: generation {next}, {candidate.RowCount("contents")} entries. Original media remains at {review.Source}; database backup {result.BackupPath}.");
            return 0;
        }
        catch (Exception ex)
        {
            if (acquired)
            {
                try { store.Update(r => r.IsOwnedBy(device, session)
                    ? Ownership.RequireRecovery(r, device, session, "Enrollment interrupted; inspect " + receipt, DateTimeOffset.UtcNow) : r,
                    TimeSpan.FromSeconds(5)); }
                catch (Exception journalError) when (journalError is IOException or MedalSyncException or UnauthorizedAccessException)
                { output.WriteLine("Ownership could not be updated: " + journalError.Message); }
            }
            throw new UnsafeOperationException($"Enrollment stopped: {ex.Message} Keep all originals and {directory}; inspect the receipt before recovering.");
        }
    }
}

internal static class EnrollmentCopy
{
    public static string Copy(string source, string target)
    {
        // Refuse links in both paths. No existing target is ever overwritten.
        RequireNoLinks(source); RequireNoLinks(target);
        string hash = Hashing.Sha256File(source);
        if (File.Exists(target))
        {
            if (Hashing.Sha256File(target) != hash) throw new UnsafeOperationException("Enrollment target already has different bytes: " + target);
            return hash;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string partial = target + ".medal-sync-" + Guid.NewGuid().ToString("N") + ".partial";
        File.Copy(source, partial, overwrite: false);
        if (Hashing.Sha256File(partial) != hash || Hashing.Sha256File(source) != hash)
            throw new UnsafeOperationException("Enrollment copy changed during verification; retained " + partial);
        File.Move(partial, target, overwrite: false);
        if (Hashing.Sha256File(target) != hash) throw new UnsafeOperationException("Final enrollment copy failed verification: " + target);
        return hash;
    }

    private static void RequireNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnsafeOperationException("Enrollment refuses linked paths: " + current);
    }
}

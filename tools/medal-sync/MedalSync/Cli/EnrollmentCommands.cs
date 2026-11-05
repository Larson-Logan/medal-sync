using System.Runtime.Versioning;
using System.Text.Json;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
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
        bool rollback = args.Flag("--rollback");
        args.RequireAllUsed();
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        var install = MedalEnvironment.Locate();
        var device = state.Device();
        if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Quit Medal from its tray before recovering enrollment.");
        var current = CoordinationStore.Open(config).ReadJournal();
        if (EnrollmentOwnership.IsPending(current) || IsInterrupted(directory) || IsHandedOff(directory, current, device))
        {
            using var guard = new Mutex(false, SessionCommands.InstanceMutex);
            bool held;
            try { held = guard.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new UnsafeOperationException("Another medal-sync operation is running on this PC.");
            try
            {
                return RecoverInterrupted(config, install, state, CoordinationStore.Open(config), device, directory, rollback, apply, output,
                    finish: () => SessionCommands.FinishOwnSession(output));
            }
            finally { guard.ReleaseMutex(); }
        }
        if (rollback) throw new UnsafeOperationException("--rollback is only for an interrupted 'enroll --apply' that still holds the library.");
        var review = Json.ReadFile<EnrollmentReview>(Path.Combine(directory, "review.json"), "Enrollment review");
        if (review.LibraryId != config.LibraryId || review.DeviceId != device.DeviceId || install.AccountId != config.AccountId)
            throw new UnsafeOperationException("Enrollment recovery belongs to another device, library or account.");
        var candidate = LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(Path.Combine(directory, "candidate.json")));
        var store = CoordinationStore.Open(config);
        var journal = store.ReadJournal();
        if (candidate.Digest() != review.CandidateDigest || store.Generations.Read(journal.CommittedGeneration).Snapshot.Digest() != review.CandidateDigest
            || journal.Owner?.DeviceId != device.DeviceId || journal.State != OwnershipState.Draining)
            throw new UnsafeOperationException("Recovery requires this PC's blocked draining session and the committed enrollment candidate.");
        var restoredLibrary = LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId);
        if (restoredLibrary.Digest() != review.SharedDigest)
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
            if (new MedalProcess(install).IsRunning() || LibraryDatabase.ReadSnapshot(install.DatabasePath, config.AccountId).Digest() != restoredLibrary.Digest())
                throw new UnsafeOperationException("Medal or the local library changed before recovery.");
            var latest = store.ReadJournal();
            if (latest != journal) throw new UnsafeOperationException("Ownership changed before recovery.");
            string evidence = Path.Combine(directory, "native-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidence);
            AtomicFile.CreateNew(Path.Combine(evidence, "before.json"), restoredLibrary.ToCanonicalJson());
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
        var install = MedalEnvironment.Locate();
        var device = state.Device();
        if (install.AccountId != config.AccountId) throw new IncompatibleLibraryException("Enrollment account differs from this PC's signed-in Medal account.");
        using var mutex = new Mutex(false, SessionCommands.InstanceMutex);
        bool locked;
        try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
        if (!locked) throw new UnsafeOperationException("Another medal-sync operation is running on this PC.");
        try
        {
            return Run(config, install, state, device, directory, apply, output);
        }
        catch (StorageOfflineException ex) when (config.PrimaryIsRclone)
        {
            // Raised only before this PC takes the library; afterwards a failure stops the apply for 'enroll recover'.
            output.WriteLine($"Waiting for storage: {ex.Message} Neither library nor ownership was changed; "
                + "run the same enroll command again once storage is back.");
            return 3;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static int Run(MedalSyncConfig config, MedalInstall install, LocalState state, DeviceIdentity device, string directory, bool apply,
        TextWriter output)
    {
        if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Quit Medal from its tray before preparing or applying enrollment.");
        var store = CoordinationStore.Open(config);
        var journal = store.ReadJournal();
        if (EnrollmentOwnership.IsPending(journal) && journal.Owner?.DeviceId == device.DeviceId)
            throw new UnsafeOperationException(EnrollmentOwnership.Interrupted(EnrollmentOwnership.PendingFolder(state.Root, journal.SessionId)));
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
        if (config.PrimaryIsRclone && (source.IsWithin(config.MountedCapture) || config.MountedCapture.IsWithin(source)))
            throw new UnsafeOperationException("Enrollment expects this PC's original capture folder, separate from its persistent local copy.");
        var destination = config.Capture.Append("Devices", device.DeviceId);
        var plan = LibraryReconciliation.Plan(shared.Snapshot, local, source, destination);
        var candidate = CountRebuilder.Rebuild(plan.Snapshot, install.DatabasePath);
        // A cloud primary is listed before anything is written, so an outage leaves the preview with nothing changed.
        IReadOnlyList<string> conflicts = config.PrimaryIsRclone
            ? [.. plan.Conflicts, .. LocalCopyConflicts(config, PrimaryStorage.Checker(config, state), plan.Files)]
            : plan.Conflicts;
        var review = new EnrollmentReview(1, config.LibraryId, device.DeviceId, journal.CommittedGeneration,
            shared.Snapshot.Digest(), local.Digest(), candidate.Digest(), source.Display, destination.Display,
            plan.Added, plan.Matched, plan.Identities, plan.Files, conflicts);
        output.WriteLine($"Union: {candidate.RowCount("contents")} entries; {plan.Added} unique entries added; {plan.Matched} shared identities matched; {conflicts.Count} conflicts.");
        foreach (string conflict in conflicts.Take(20)) output.WriteLine("  " + conflict);
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
            if (config.PrimaryIsRclone)
                output.WriteLine($"With --apply, files are copied to this PC's local copy ({config.MountedCapture.Display}). This PC then keeps the library "
                    + $"until finish has uploaded and verified every clip on primary storage {config.PrimaryCapture}.");
            return conflicts.Count == 0 ? 0 : 1;
        }
        var approved = Json.ReadFile<EnrollmentReview>(Path.Combine(directory, "review.json"), "Enrollment review");
        if (Hashing.Sha256Hex(Json.Serialize(approved)) != Hashing.Sha256Hex(Json.Serialize(review)))
            throw new UnsafeOperationException("The library or enrollment plan changed since the preview. Prepare a fresh review; nothing was changed.");
        if (conflicts.Count != 0) throw new UnsafeOperationException("Enrollment has unresolved conflicts; neither library was changed.");
        return Apply(config, install, state, store, device, review, candidate, directory, output,
            finish: () => SessionCommands.FinishOwnSession(output));
    }

    /// <summary>
    /// Enrollment targets that a cloud primary or this PC's local copy already holds. A file on the primary is never
    /// replaced, so any file there is a conflict; a local file is reused only when its bytes match the original.
    /// </summary>
    internal static IReadOnlyList<string> LocalCopyConflicts(MedalSyncConfig config, IAssetChecker primary, IEnumerable<EnrollmentFile> files)
    {
        primary.RequireServer();
        var conflicts = new List<string>();
        foreach (var file in files)
        {
            if (primary.ExistsOnServer(file.CapturePath))
                conflicts.Add($"{file.CapturePath}: already on primary storage {config.PrimaryCapture}; enrollment never replaces a file there.");
            string local = LocalTarget(config, file);
            if (Directory.Exists(local)) conflicts.Add($"{file.CapturePath}: this PC's local copy has a folder at {local}.");
            else if (File.Exists(local) && File.Exists(file.Source)
                && (new FileInfo(local).Length != new FileInfo(file.Source).Length || Hashing.Sha256File(local) != Hashing.Sha256File(file.Source)))
                conflicts.Add($"{file.CapturePath}: this PC's local copy already has different bytes at {local}.");
        }
        return conflicts;
    }

    /// <summary>Where a cloud-primary enrollment copies a file: the persistent local copy that the clip drive serves.</summary>
    private static string LocalTarget(MedalSyncConfig config, EnrollmentFile file) =>
        WindowsPath.Parse(file.CapturePath).Rebase(config.Capture, config.MountedCapture).Display;

    /// <summary>Where an apply stood when it last saved <c>apply.json</c>.</summary>
    internal sealed record EnrollmentReceipt(int Format, string Stage, string Session, string LocalDigest, string SharedDigest,
        string CandidateDigest, string Source, string Destination, long? Generation, DateTimeOffset At);

    /// <summary>Stages an interrupted apply can continue from, or roll back from.</summary>
    internal static readonly string[] InterruptedStages = ["prepared", "copying", "copied", "restored", "capture-configured", HandingOff];

    /// <summary>A cloud-primary apply saves this before giving its session to the normal finish, and <see cref="HandedOff"/> after.</summary>
    internal const string HandingOff = "handing-off";
    internal const string HandedOff = "handed-off";

    /// <param name="resume">The receipt of this PC's interrupted apply, when continuing it; its ownership is already held.</param>
    /// <param name="stopAfter">Tests only: return after saving this stage, as a killed process would.</param>
    /// <param name="primary">Tests only: the cloud primary's checker instead of a real rclone listing.</param>
    /// <param name="finish">
    /// Runs the normal finish of the session a cloud-primary apply hands over. Null leaves that session held and
    /// draining for a later finish, as a killed process would.
    /// </param>
    internal static int Apply(MedalSyncConfig config, MedalInstall install, LocalState state, CoordinationStore store,
        DeviceIdentity device, EnrollmentReview review, LibrarySnapshot candidate, string directory, TextWriter output,
        EnrollmentReceipt? resume = null, LibrarySnapshot? original = null, string? stopAfter = null,
        IAssetChecker? primary = null, Func<int>? finish = null)
    {
        // The receipt is written before acquiring ownership. An interrupted apply
        // is never replayed blindly; all copies and the original DB remain.
        string receipt = Path.Combine(directory, "apply.json");
        if (resume is null && File.Exists(receipt)) throw new UnsafeOperationException("This enrollment was already attempted. Inspect apply.json and run 'medal-sync enroll recover' for it before another apply.");
        if (Directory.Exists(config.DriveRoot.Display)) throw new UnsafeOperationException("The virtual drive is occupied. Stop its clean session first.");
        // With a cloud primary, files go to the persistent local copy the clip drive serves. Only the following finish
        // uploads and verifies them, so the union is checked there and enrollment never publishes it itself.
        bool cloud = config.PrimaryIsRclone;
        primary ??= PrimaryStorage.Checker(config, state);
        primary.RequireServer();
        IAssetChecker checker = cloud ? new ShareAssetChecker(config.Capture, config.MountedCapture, config.LibraryId, new FileSystemProbe()) : primary;
        // Where an interrupted apply actually stopped; the receipt can lag one step behind.
        bool restored = resume is not null && CurrentDigest(install) == review.CandidateDigest;
        bool captured = resume is not null && CaptureIs(install, config.Capture.Display);
        var originalFiles = AssetInventory.Check(AssetInventory.Collect(original ?? LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId)),
            [WindowsPath.Parse(review.Source)], new FileSystemProbe());
        if (originalFiles.Blocking.Any()) throw new UnsafeOperationException("An original local asset is missing or unsafe; enrollment did not " + (resume is null ? "begin." : "continue."));
        var optionalMissing = originalFiles.Items.Where(a => a.Reference.Role == AssetRole.Optional && a.Problem == AssetProblem.Missing)
            .Select(a => a.Reference.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copies = review.Files.Where(f => !optionalMissing.Contains(f.Source)).ToArray();
        string Target(EnrollmentFile file) => cloud ? LocalTarget(config, file)
            : WindowsPath.Parse(file.CapturePath).Rebase(config.Capture, config.ServerCapture).Display;
        if (cloud)
        {
            if (copies.FirstOrDefault(f => primary.ExistsOnServer(f.CapturePath)) is { } taken)
                throw new UnsafeOperationException($"{taken.CapturePath} is already on primary storage {config.PrimaryCapture}; enrollment never replaces it. Nothing was changed.");
            var shared = store.Generations.Read(review.Generation).Snapshot;
            if (shared.Digest() != review.SharedDigest) throw new UnsafeOperationException("The shared library changed since the review. Prepare a fresh preview.");
            // As a launch would: claim this PC's local copy and download the committed clips it lacks, so the
            // following finish can verify the whole union from here. An outage that needs downloads waits here.
            new StorageReplication(config, state, () =>
            {
                if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Medal reopened during enrollment.");
            }, primary: primary).Prepare(shared);
        }
        // Files already copied by an interrupted apply need no new space.
        long bytes = copies.Where(f => !File.Exists(Target(f))).Sum(f => new FileInfo(f.Source).Length);
        long? free = cloud ? StorageVolume.RequirePresent(config.MountedCapture.Display, "persistent clip storage", StorageVolume.Describe).FreeBytes
            : Inventory.FreeSpace(config.ServerCapture.Display)?.Free;
        if (free is null || free < bytes + (20L << 30))
            throw new UnsafeOperationException($"Cannot verify {(cloud ? "local copy" : "server")} space for enrollment plus a 20 GiB reserve.");
        string session = resume?.Session ?? Guid.NewGuid().ToString("N");
        bool Save(string stage, long? generation = null)
        {
            AtomicFile.Replace(receipt, Json.Serialize(new EnrollmentReceipt(1, stage, session, review.LocalDigest, review.SharedDigest,
                review.CandidateDigest, review.Source, review.Destination, generation, DateTimeOffset.UtcNow)));
            return stage == stopAfter;
        }
        if (resume is null)
        {
            using (var db = LibraryDatabase.OpenReadOnly(install.DatabasePath)) DatabaseBackup.Create(db, directory, install.AccountId, DateTimeOffset.UtcNow);
            if (Save("prepared")) return -1;
            EnrollmentOwnership.WritePending(state.Root, session, directory);
        }
        bool acquired = resume is not null;
        bool handedOff = false;
        RestoreResult? result = null;
        try
        {
            if (resume is null)
            {
                // Marked as enrollment, so launch and finish never give it back as an aborted start.
                store.Update(r => r.CommittedGeneration == review.Generation
                    ? Ownership.Acquire(r, device, session, DateTimeOffset.UtcNow) with { Reason = EnrollmentOwnership.Reason(session) }
                    : throw new UnsafeOperationException("Another PC published since the review. Prepare a fresh preview."), TimeSpan.FromSeconds(5));
                acquired = true;
            }
            if (!restored)
            {
                if (Save("copying")) return -1;
                int done = 0;
                foreach (var file in copies)
                {
                    if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Medal reopened during enrollment. Quit it before recovery.");
                    string target = Target(file);
                    // An existing target with the same bytes is a verified copy from an interrupted apply; it is reused.
                    string hash = EnrollmentCopy.Copy(file.Source, target);
                    File.AppendAllText(Path.Combine(directory, "copies.jsonl"), JsonSerializer.Serialize(new { file.Source, target, sha256 = hash }) + "\n");
                    done++;
                    if (done % 25 == 0 || done == copies.Length) output.WriteLine($"Verified {done}/{copies.Length} files ({bytes / 1e9:0.0} GB to copy).");
                }
                if (Save("copied")) return -1;
            }
            if (checker.OnServer(candidate).Blocking.Any())
                throw new UnsafeOperationException(cloud ? "The merged library has files missing from this PC's local copy. Originals and copies are retained."
                    : "The merged library has unavailable server assets. Originals and copies are retained.");
            if (!restored)
            {
                if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Medal reopened before library restore.");
                if (CurrentDigest(install) != review.LocalDigest)
                    throw new UnsafeOperationException("The local library changed during enrollment; the original and copied files are retained.");
                result = LibraryRestorer.Restore(install.DatabasePath, candidate, new RestoreOptions(install.AccountId, directory), DateTimeOffset.UtcNow);
            }
            if (Save("restored")) return -1;
            if (!captured) CaptureFolderSetting.Change(install.DatabasePath, review.Source, config.Capture.Display);
            if (Save("capture-configured")) return -1;
            if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Medal reopened before publication.");
            if (CurrentDigest(install) != candidate.Digest())
                throw new UnsafeOperationException("The merged library changed before publication.");
            if (cloud)
            {
                // The session leaves enrollment's protection and becomes this PC's ordinary draining session. From here only
                // the normal finish moves it on: it uploads and byte-verifies every referenced clip, then publishes and releases.
                if (Save(HandingOff)) return -1;
                // Either step clears the enrollment reason; a session stopped between them is an ordinary active one that finish drains.
                store.Update(r =>
                {
                    if (!r.IsOwnedBy(device, session) || !EnrollmentOwnership.IsPending(r))
                        throw new OwnershipConflictException("Ownership changed before enrollment handed its session to finish.");
                    return r.State == OwnershipState.Starting ? Ownership.MarkActive(r, device, session, DateTimeOffset.UtcNow)
                        : Ownership.BeginDrain(r, device, session, DateTimeOffset.UtcNow);
                }, TimeSpan.FromSeconds(5));
                handedOff = true;
                store.Update(r => Ownership.BeginDrain(r, device, session, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5));
                Save(HandedOff);
                EnrollmentOwnership.ClearPending(state.Root);
                string backups = result?.BackupPath is { } saved ? $"Database backup {saved}." : $"Database backups are in {directory}.";
                output.WriteLine($"Enrolled on this PC: {candidate.RowCount("contents")} entries, with {copies.Length} file(s) in its local copy. "
                    + $"Original media remains at {review.Source}. {backups} This PC keeps the library until finish has uploaded and verified "
                    + $"every clip on primary storage {config.PrimaryCapture}; only then is the union published.");
            }
            else
            {
                // After a caught failure the journal is RECOVERY_REQUIRED, which drains directly.
                store.Update(r => r.State == OwnershipState.Starting ? Ownership.MarkActive(r, device, session, DateTimeOffset.UtcNow) : r, TimeSpan.FromSeconds(5));
                store.Update(r => Ownership.BeginDrain(r, device, session, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5));
                store.Update(r => Ownership.BeginPublish(r, device, session, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5));
                long next = store.Generations.Next(review.Generation);
                store.Generations.Publish(next, review.Generation, session, device, candidate, AssetManifest.From(checker.OnServer(candidate)), install.Versions, DateTimeOffset.UtcNow);
                store.Update(r => Ownership.CompletePublish(r, device, session, next, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5));
                Save("completed", next);
                EnrollmentOwnership.ClearPending(state.Root);
                string backup = result?.BackupPath is { } path ? $"; database backup {path}" : $"; database backups are in {directory}";
                output.WriteLine($"Enrolled: generation {next}, {candidate.RowCount("contents")} entries. Original media remains at {review.Source}{backup}.");
                return 0;
            }
        }
        catch (Exception ex) when (handedOff)
        {
            // The journal already names an ordinary session; marking it as enrollment again would hide it from finish.
            throw new UnsafeOperationException($"Enrollment handed this PC's session to finish, then stopped: {ex.Message} "
                + "Run 'medal-sync finish' (or open Medal Library) to upload, verify and publish the union.");
        }
        catch (Exception ex)
        {
            // Nothing was taken, so there is nothing for launch and finish to point at.
            if (!acquired) EnrollmentOwnership.ClearPending(state.Root);
            if (acquired)
            {
                try { store.Update(r => r.IsOwnedBy(device, session)
                    ? Ownership.RequireRecovery(r, device, session, EnrollmentOwnership.Reason(session) + " stopped; run 'medal-sync enroll recover' on " + device.DisplayName, DateTimeOffset.UtcNow) : r,
                    TimeSpan.FromSeconds(5)); }
                catch (Exception journalError) when (journalError is IOException or MedalSyncException or UnauthorizedAccessException)
                { output.WriteLine("Ownership could not be updated: " + journalError.Message); }
            }
            throw new UnsafeOperationException($"Enrollment stopped: {ex.Message} Keep all originals and {directory}; run 'medal-sync enroll recover --out \"{directory}\"' to continue or roll back.");
        }
        return FinishHandedOff(output, finish);
    }

    /// <summary>The normal finish of a handed-off session. An outage there is its ordinary pending wait: this PC keeps the library and clips.</summary>
    private static int FinishHandedOff(TextWriter output, Func<int>? finish)
    {
        if (finish is null) return 0;
        try { return finish(); }
        catch (StorageOfflineException ex)
        {
            output.WriteLine($"Waiting for storage: {ex.Message} This PC keeps the library and its clips; "
                + "the switch finishes automatically when Medal Library is opened, or run 'medal-sync finish'.");
            return 3;
        }
    }

    /// <summary>
    /// An interrupted <c>enroll --apply</c>: this PC still holds its enrollment ownership. Continues from where it stopped,
    /// reusing verified copies, or with <paramref name="rollback"/> puts back this PC's original library rows and capture
    /// folder and gives the library back. Copies already on the server are kept either way.
    /// A cloud-primary apply that already handed its session to finish is not rolled back here: recovery runs that finish.
    /// </summary>
    /// <param name="finish">Runs the normal finish of this PC's session; null in tests.</param>
    /// <param name="primary">Tests only: the cloud primary's checker instead of a real rclone listing.</param>
    internal static int RecoverInterrupted(MedalSyncConfig config, MedalInstall install, LocalState state, CoordinationStore store,
        DeviceIdentity device, string directory, bool rollback, bool apply, TextWriter output, Func<int>? finish = null,
        IAssetChecker? primary = null)
    {
        var review = Json.ReadFile<EnrollmentReview>(Path.Combine(directory, "review.json"), "Enrollment review");
        var receipt = Json.ReadFile<EnrollmentReceipt>(Path.Combine(directory, "apply.json"), "Enrollment apply receipt");
        if (review.LibraryId != config.LibraryId || review.DeviceId != device.DeviceId || install.AccountId != config.AccountId)
            throw new UnsafeOperationException("This enrollment review belongs to another device, library or account.");
        bool stage = InterruptedStages.Contains(receipt.Stage) || receipt.Stage == HandedOff;
        if (receipt.Format != 1 || !stage || receipt.LocalDigest != review.LocalDigest
            || receipt.CandidateDigest != review.CandidateDigest || receipt.SharedDigest != review.SharedDigest)
            throw new UnsafeOperationException($"apply.json in {directory} is not an interrupted apply of this review (stage '{receipt.Stage}').");
        var journal = store.ReadJournal();
        if (IsHandedOff(receipt, journal, device))
        {
            output.WriteLine($"This enrollment already handed session {receipt.Session} to the normal finish; it is no longer an enrollment to continue or roll back. "
                + $"Finish uploads and verifies every clip on primary storage {config.PrimaryCapture}, then publishes the union and gives the library back.");
            if (rollback) throw new UnsafeOperationException("--rollback is only for an enrollment that has not been handed to finish. Run 'medal-sync finish' instead; nothing was changed.");
            if (!apply) { output.WriteLine("Preview only. Add --apply with Medal quit to run that finish now, or run 'medal-sync finish'."); return 0; }
            return FinishHandedOff(output, finish);
        }
        if (receipt.Stage == HandedOff)
            throw new UnsafeOperationException($"apply.json in {directory} is for session {receipt.Session}, which this PC no longer holds. Nothing was changed.");
        if (!journal.IsOwnedBy(device, receipt.Session) || !EnrollmentOwnership.IsPending(journal)
            || journal.State is not (OwnershipState.Starting or OwnershipState.RecoveryRequired))
        {
            if (receipt.Stage == "prepared" && journal.Owner?.DeviceId != device.DeviceId)
                throw new UnsafeOperationException("That apply stopped before it took the library; nothing was changed. Prepare a fresh 'enroll' preview in a new folder.");
            throw new UnsafeOperationException($"apply.json in {directory} is for session {receipt.Session}, but this PC does not hold that enrollment. Nothing was changed.");
        }
        var candidate = LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(Path.Combine(directory, "candidate.json")));
        var original = LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(Path.Combine(directory, "local-before.json")));
        if (candidate.Digest() != review.CandidateDigest || original.Digest() != review.LocalDigest)
            throw new UnsafeOperationException("The saved review files changed; recovery was refused.");
        if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Quit Medal from its tray before recovering enrollment.");

        string digest = CurrentDigest(install);
        bool restored = digest == review.CandidateDigest;
        if (!restored && digest != review.LocalDigest)
            throw new UnsafeOperationException("This PC's library is neither its original nor the reviewed union. Nothing was changed; keep the review folder and investigate.");
        bool captured = CaptureIs(install, config.Capture.Display);
        if (!captured && !CaptureIs(install, review.Source))
            throw new UnsafeOperationException("Medal's capture folder is neither the original nor the shared one. Nothing was changed.");
        // The original library with the shared capture folder is a rollback stopped between its two steps; either way works from it.
        string where = captured && restored ? "the union is restored and Medal records to the shared drive"
            : captured ? "a rollback stopped part-way: this PC's original library is back, but Medal still records to the shared drive"
            : restored ? "the union is restored; Medal still records to the original folder"
            : "this PC's original library is unchanged; some files may already be copied";
        output.WriteLine($"Enrollment stopped at '{receipt.Stage}': {where}.");
        if (!rollback)
        {
            output.WriteLine("Recovery continues the enrollment: verified copies are reused, then the union is restored and published.");
            if (!apply) { output.WriteLine("Preview only. Add --apply with Medal quit to continue, or --rollback to undo it instead."); return 0; }
            return Apply(config, install, state, store, device, review, candidate, directory, output, resume: receipt, original: original,
                primary: primary, finish: finish);
        }
        output.WriteLine($"Rollback puts back this PC's original library and capture folder ({review.Source}) and gives the library back. "
            + $"Files already copied under {review.Destination} {(config.PrimaryIsRclone ? "in this PC's local copy" : "on the server")} are kept; they are not in the shared library.");
        if (!apply) { output.WriteLine("Preview only. Add --apply with Medal quit to roll back."); return 0; }
        if (restored) LibraryRestorer.Restore(install.DatabasePath, original,
            new RestoreOptions(install.AccountId, directory, AllowEmptyReplacement: true), DateTimeOffset.UtcNow);
        if (captured) CaptureFolderSetting.Change(install.DatabasePath, config.Capture.Display, review.Source);
        if (CurrentDigest(install) != review.LocalDigest || !CaptureIs(install, review.Source))
            throw new UnsafeOperationException("Rollback did not read back as the original library and capture folder; ownership is kept.");
        store.Update(r =>
        {
            if (!r.IsOwnedBy(device, receipt.Session) || !EnrollmentOwnership.IsPending(r)) throw new OwnershipConflictException("Ownership changed during rollback.");
            return r.State == OwnershipState.Starting
                ? Ownership.AbortStart(r, device, receipt.Session, "enrollment rolled back by its own PC", DateTimeOffset.UtcNow)
                : Ownership.Abandon(r, device, receipt.Session, "enrollment rolled back by its own PC", DateTimeOffset.UtcNow);
        }, TimeSpan.FromSeconds(5));
        AtomicFile.Replace(Path.Combine(directory, "apply.json"), Json.Serialize(receipt with { Stage = "rolled-back", At = DateTimeOffset.UtcNow }));
        EnrollmentOwnership.ClearPending(state.Root);
        output.WriteLine("Rolled back. The library is Ready and this PC's original library and capture folder are back. Prepare a fresh 'enroll' preview in a new folder to try again.");
        return 0;
    }

    /// <summary>Whether the review folder holds an apply that stopped before completing.</summary>
    private static bool IsInterrupted(string directory)
    {
        string path = Path.Combine(directory, "apply.json");
        if (!File.Exists(path)) return false;
        return InterruptedStages.Contains(Json.ReadFile<EnrollmentReceipt>(path, "Enrollment apply receipt").Stage);
    }

    /// <summary>Whether the review folder holds a cloud-primary apply whose session this PC still holds as an ordinary, unfinished session.</summary>
    private static bool IsHandedOff(string directory, OwnershipRecord journal, DeviceIdentity device)
    {
        string path = Path.Combine(directory, "apply.json");
        return File.Exists(path) && IsHandedOff(Json.ReadFile<EnrollmentReceipt>(path, "Enrollment apply receipt"), journal, device);
    }

    /// <summary>
    /// A kill between saving <see cref="HandingOff"/> and changing the journal leaves enrollment ownership, which continues
    /// normally; once the journal changed, the session belongs to finish even if the receipt still says <see cref="HandingOff"/>.
    /// </summary>
    private static bool IsHandedOff(EnrollmentReceipt receipt, OwnershipRecord journal, DeviceIdentity device) =>
        receipt.Stage is HandingOff or HandedOff && journal.IsOwnedBy(device, receipt.Session) && !EnrollmentOwnership.IsPending(journal);

    private static string CurrentDigest(MedalInstall install) => LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId).Digest();

    private static bool CaptureIs(MedalInstall install, string folder)
    {
        using var connection = LibraryDatabase.OpenReadOnly(install.DatabasePath);
        string? current = JsonSerializer.Deserialize<string>(LibraryDatabase.ReadSetting(connection, null, "ClipFolder") ?? "null");
        return current is not null && WindowsPath.TryParse(current, out var path, out _) && path.Equals(WindowsPath.Parse(folder));
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

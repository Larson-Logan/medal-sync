using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

internal sealed record MigrationGeneration(long Number, string ManifestSha256);
internal sealed record MigrationMedia(string Path, string? SourceSha256, bool AlreadyOnDrive);
internal sealed record StorageMigrationPlan(int Format, string Id, string LibraryId, string DeviceId, string SourceConfigSha256,
    string TargetFolderId, MedalSyncConfig Target, long Head, long Revision, string? RetentionSha256,
    IReadOnlyList<DeviceIdentity> Devices, IReadOnlyList<MigrationGeneration> Generations, IReadOnlyList<MigrationMedia> Media, string Integrity = "");

/// <summary>Moves cloud authority only after a frozen destination can prove the source's committed history.</summary>
internal sealed class StorageMigration(MedalSyncConfig source, LocalState state, DeviceIdentity device,
    CoordinationStore store, MedalSyncConfig target, CloudCoordination cloud, Action guard,
    Func<IReadOnlyList<string>, string>? execute = null)
{
    private readonly string credentials = Environment.ExpandEnvironmentVariables(target.Replication!.RcloneConfigPath!);

    public StorageMigrationPlan Preview(string configHash)
    {
        var journal = store.ReadJournal();
        if (journal.State != OwnershipState.Clean) throw new UnsafeOperationException("Finish or recover the library before reviewing a migration.");
        if (cloud.HasJournal()) throw new UnsafeOperationException("This destination journal is already in use. Allocate a new Google Drive journal ID.");
        string folder = cloud.StorageIdentity();
        if (store.CloudStorageIdentity() == folder)
            throw new UnsafeOperationException("Choose a different Google Drive storage folder for the migration.");
        var evidence = Evidence(journal.CommittedGeneration);
        var existing = Hashes(target.PrimaryCapture, evidence.Media.Where(file => file.SourceSha256 is not null).Select(file => file.Path), allowMissing: true);
        var media = evidence.Media.Select(file =>
        {
            bool present = existing.TryGetValue(file.Path, out string? hash);
            if (present && hash != file.SourceSha256)
                throw new UnsafeOperationException($"Google Drive holds different bytes at '{file.Path}'. Pick an empty destination or resolve that file before reviewing.");
            return file with { AlreadyOnDrive = present };
        }).ToArray();
        var devices = evidence.Devices.Concat(store.PendingRequests(DateTimeOffset.UtcNow).Select(request => request.Device))
            .Append(store.Identity.CreatedBy).Append(device).DistinctBy(pc => pc.DeviceId).OrderBy(pc => pc.DisplayName, StringComparer.Ordinal).ToArray();
        if (store.ReadJournal() != journal) throw new UnsafeOperationException("The library changed during review. Create a new migration review.");
        var plan = new StorageMigrationPlan(1, Guid.NewGuid().ToString("N"), source.LibraryId, device.DeviceId, configHash, folder,
            target, journal.CommittedGeneration, journal.Revision, RetentionHash(evidence.Retention), devices, evidence.Generations, media);
        return plan with { Integrity = Digest(plan) };
    }

    internal static void Validate(StorageMigrationPlan plan, MedalSyncConfig source, DeviceIdentity device, string configHash)
    {
        if (plan.Format != 1 || !CoordinationStore.IsValidId(plan.Id) || plan.LibraryId != source.LibraryId || plan.DeviceId != device.DeviceId
            || plan.SourceConfigSha256 != configHash || plan.Target is null || plan.Generations is null || plan.Media is null || plan.Devices is null
            || plan.Integrity != Digest(plan))
            throw new UnsafeOperationException("The migration review was edited or belongs to another configuration or PC. Review again on the source PC.");
        if (plan.Target.LibraryId != source.LibraryId || plan.Target.AccountId != source.AccountId
            || !plan.Target.Capture.Equals(source.Capture) || plan.Target.StorageMode != StorageMode.Cloud
            || string.IsNullOrWhiteSpace(plan.Target.CloudJournalId) || plan.Target.CloudJournalId.Contains('.'))
            throw new UnsafeOperationException("The migration destination must retain the same account, library and capture path with a Google Drive journal.");
        plan.Target.Validate();
    }

    public void Apply(StorageMigrationPlan plan, bool rollback)
    {
        using var writer = store.IsCloud ? null : store.AcquireWriterLock(TimeSpan.FromSeconds(10));
        var current = store.ReadJournal();
        bool Matches(OwnershipRecord record) => record.IsOwnedBy(device, plan.Id) && record.OperationDigest == plan.Integrity
            && record.CommittedGeneration == plan.Head;
        if (rollback)
        {
            if (current.State == OwnershipState.Clean && current.CommittedGeneration == plan.Head && current.Revision == plan.Revision) return;
            if (current.State != OwnershipState.Migrating || !Matches(current))
                throw new UnsafeOperationException("Only this PC's unfinished migration can be rolled back before the source is retired.");
            store.UpdateUnderWriterLock(record => record == current
                ? Ownership.EndMigration(record, device, plan.Id, rollback: true, DateTimeOffset.UtcNow)
                : throw new OwnershipConflictException("The migration changed. Review current ownership before retrying."), guard);
            return;
        }
        if (cloud.StorageIdentity() != plan.TargetFolderId)
            throw new UnsafeOperationException("The destination folder changed after review. Restore the reviewed destination before retrying.");
        if (store.CloudStorageIdentity() == plan.TargetFolderId)
            throw new UnsafeOperationException("The source and destination must use different cloud storage folders.");
        if (current.State == OwnershipState.Migrated && Matches(current))
        {
            cloud.ActivateMigration(current, device, plan.Id);
            return;
        }

        if (current.State == OwnershipState.Clean)
        {
            if (current.CommittedGeneration != plan.Head || current.Revision != plan.Revision)
                throw new UnsafeOperationException("The source library changed after review. Preview the migration again.");
            if (cloud.HasJournal()) throw new UnsafeOperationException("The destination journal is already in use. Preview with a new journal ID.");
            var review = current;
            current = store.UpdateUnderWriterLock(record => record == review
                ? Ownership.BeginMigration(record, device, plan.Id, plan.Integrity, DateTimeOffset.UtcNow)
                : throw new OwnershipConflictException("The source library changed after review. Preview again."), guard);
        }
        if (current.State != OwnershipState.Migrating || !Matches(current))
            throw new UnsafeOperationException("This review does not hold the source library. Recover or finish the current operation first.");

        void RequireSource()
        {
            guard();
            if (store.ReadJournal() != current) throw new OwnershipConflictException("The source migration changed. Copying stopped.");
        }
        RequireSource();
        var primary = source.PrimaryIsRclone ? new RcloneAssetChecker(source, state, execute, guard) : PrimaryStorage.Checker(source, state, guard);
        primary.RequireServer();
        var evidence = Evidence(plan.Head);
        if (!evidence.Generations.SequenceEqual(plan.Generations) || RetentionHash(evidence.Retention) != plan.RetentionSha256
            || !evidence.Media.SequenceEqual(plan.Media.Select(file => file with { AlreadyOnDrive = false })))
            throw new UnsafeOperationException("Source history or media changed after review. Roll back and create a new migration review.");

        var checker = new RcloneAssetChecker(target, state, execute, guard);
        checker.Initialize(allowExistingMedia: true);
        var wanted = plan.Media.Where(file => file.SourceSha256 is not null).ToArray();
        var existing = Hashes(target.PrimaryCapture, wanted.Select(file => file.Path), allowMissing: true);
        if (wanted.Any(file => existing.TryGetValue(file.Path, out string? hash) && file.SourceSha256 != hash))
            throw new UnsafeOperationException("Google Drive contains different bytes at a reviewed path. The source remains authoritative.");
        WithFiles(wanted.Where(file => !existing.ContainsKey(file.Path)).Select(file => file.Path), list =>
            Run("copy", source.PrimaryCapture, target.PrimaryCapture, "--files-from-raw", list, "--immutable", "--checksum"));
        var verified = Hashes(target.PrimaryCapture, wanted.Select(file => file.Path), allowMissing: false);
        if (wanted.Any(file => !verified.TryGetValue(file.Path, out string? hash) || file.SourceSha256 != hash))
            throw new UnsafeOperationException("The destination did not verify against the reviewed media. The source remains authoritative.");

        foreach (var generation in plan.Generations)
        {
            RequireSource();
            cloud.UploadFrom(store.Generations, generation.Number, guard);
        }
        RequireSource();
        string headHash = plan.Generations.Single(generation => generation.Number == plan.Head).ManifestSha256;
        cloud.StageMigration(store.Identity, current, headHash, evidence.Retention);
        // The destination is still frozen. Verify it independently before retiring the source.
        cloud.Download();
        RequireSource();
        var retired = store.UpdateUnderWriterLock(record => record == current
            ? Ownership.EndMigration(record, device, plan.Id, rollback: false, DateTimeOffset.UtcNow)
            : throw new OwnershipConflictException("The source migration changed before retirement."), guard);
        cloud.ActivateMigration(retired, device, plan.Id);
    }

    private (MigrationGeneration[] Generations, DeviceIdentity[] Devices, MigrationMedia[] Media, GenerationRetention? Retention) Evidence(long head)
    {
        guard();
        store.RefreshHistory();
        var retention = store.Generations.ReadRetention();
        var ancestry = store.Generations.Chain(head).Select(generation => generation.Generation).ToArray();
        var generations = new List<MigrationGeneration>();
        var devices = new List<DeviceIdentity>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (long number in store.Generations.Numbered())
        {
            if (retention?.Find(number) is not null) continue;
            string folder = Path.Combine(store.Generations.Directory, GenerationStore.Name(number));
            RequireOrdinary(folder);
            foreach (string file in new[] { "manifest.json", "library.json", "assets.json" }) RequireOrdinary(Path.Combine(folder, file));
            var generation = store.Generations.Read(number);
            generations.Add(new(number, Hashing.Sha256File(Path.Combine(folder, "manifest.json"))));
            devices.Add(generation.Manifest.Device);
            foreach (var (relative, required) in ReplicationPlan.Media(generation.Snapshot, source.Capture))
                paths[relative] = required || paths.GetValueOrDefault(relative);
        }
        if (generations.All(g => g.Number != head)) throw new InvalidDataException("The committed head was not found in retained history.");
        long highest = store.Generations.Next(head) - 1;
        if (highest > generations.Max(generation => generation.Number) && (retention?.HighWater ?? -1) < highest)
            retention = new(GenerationRetention.CurrentFormat, highest, ancestry, retention?.Pruned ?? []);
        if (!source.PrimaryIsRclone)
            foreach (string relative in paths.Keys)
                RequireOrdinary(source.ServerCapture.Append(relative.Split('/')).Display);
        var found = Hashes(source.PrimaryCapture, paths.Keys, allowMissing: true);
        if (paths.Any(file => file.Value && !found.ContainsKey(file.Key)))
            throw new UnsafeOperationException("Media required by a retained generation is missing on the source. Recover it before migrating.");
        var media = paths.Keys.Order(StringComparer.Ordinal).Select(path => new MigrationMedia(path, found.GetValueOrDefault(path), false)).ToArray();
        return (generations.ToArray(), devices.ToArray(), media, retention);
    }

    private Dictionary<string, string> Hashes(string root, IEnumerable<string> paths, bool allowMissing)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        WithFiles(names, list =>
        {
            string output;
            try { output = Run("hashsum", "SHA256", root, "--download", "--files-from-raw", list); }
            catch (RcloneFailedException ex) when (allowMissing && ex.ExitCode == 3) { return; }
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string text = line.TrimEnd('\r');
                if (text.Length < 67 || text.Substring(64, 2) != "  " || !text[..64].All(char.IsAsciiHexDigit)
                    || !names.Contains(text[66..]) || !result.TryAdd(text[66..], text[..64].ToLowerInvariant()))
                    throw new InvalidDataException("The storage provider returned ambiguous or incomplete media checksums.");
            }
        });
        return result;
    }

    private string Run(params string[] args)
    {
        guard();
        IReadOnlyList<string> command = [.. args, "--config", credentials, "--retries", "1", "--low-level-retries", "2", "--contimeout", "10s", "--timeout", "60s"];
        return execute is not null ? execute(command)
            : new RcloneRunner(target.ExpandedRclonePath).Run(command, TimeSpan.FromMinutes(target.Replication!.TimeoutMinutes), guard, captureOutput: true);
    }

    private static void WithFiles(IEnumerable<string> paths, Action<string> operation)
    {
        string[] names = paths.ToArray();
        if (names.Length == 0) return;
        string path = Path.Combine(Path.GetTempPath(), "medal-sync-migration-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllLines(path, names); operation(path); }
        finally { File.Delete(path); }
    }

    private static void RequireOrdinary(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnsafeOperationException("Migration will not follow a directory or file link.");
        }
    }

    private static string? RetentionHash(GenerationRetention? retention) => retention is null ? null : Hashing.Sha256Hex(Json.Serialize(retention));

    private static string Digest(StorageMigrationPlan plan) => Hashing.Sha256Hex(Json.Serialize(plan with { Integrity = "" }));
}

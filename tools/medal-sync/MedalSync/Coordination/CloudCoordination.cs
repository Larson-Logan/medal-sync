using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Util;

namespace MedalSync.Coordination;

internal sealed record CloudGenerationAttempt(long Number, DateTimeOffset? LatestWrite, long Bytes, bool ContainsOnlyGenerationFiles);

/// <summary>The provider journal is authoritative; local files are only an immutable generation cache.</summary>
internal sealed class CloudCoordination(MedalSyncConfig config, ICloudJournal api,
    Func<IReadOnlyList<string>, Action?, string> run)
{
    internal CloudCoordination(MedalSyncConfig config, ICloudJournal api, Func<IReadOnlyList<string>, string> run)
        : this(config, api, (arguments, check) => { check?.Invoke(); return run(arguments); }) { }

    private string LibraryRemote => config.Remote.TrimEnd('/') + "/.medal-sync/" + config.LibraryId;
    private string GenerationsRemote => LibraryRemote + "/generations";
    private string GenerationsLocal => Path.Combine(config.LibraryCoordinationDirectory, "generations");
    private string RetentionLocal => Path.Combine(GenerationsLocal, "retention.json");
    private string RetentionRemote(string hash) => LibraryRemote + "/retention/" + hash + ".json";
    private string? storageFolderId;
    public long HighWater { get; private set; } = -1;
    private IReadOnlyList<CloudGenerationAttempt> remoteAttempts = [];
    private static readonly string[] GenerationFiles = ["library.json", "assets.json", "manifest.json"];
    private (long Generation, string Hash, Exception Error, DateTimeOffset Until)? downloadRefusal;
    internal bool HasHistoryRefusal => downloadRefusal is not null;
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Drive permits duplicate names. Refuse ambiguous paths before copying; reserve even incomplete attempts' numbers.</summary>
    private Dictionary<string, string?> InspectGenerations(bool allowMissing = false, bool hashes = false, Action? check = null)
    {
        string listing;
        try { listing = Run("lsjson", check, [GenerationsRemote, "--recursive", "--no-mimetype", .. hashes && api.HasMd5 ? new[] { "--hash-type", "MD5" } : Array.Empty<string>()]); }
        catch (RcloneFailedException ex) when (allowMissing && ex.ExitCode == 3) { return []; }
        try
        {
            using var json = JsonDocument.Parse(listing);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, string?>(StringComparer.Ordinal);
            var attempts = new Dictionary<long, CloudGenerationAttempt>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                string? path = item.GetProperty("Path").GetString();
                if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Split('/').Any(s => s is "" or "." or "..")
                    || !paths.Add(path) || item.TryGetProperty("IsLink", out var link) && link.ValueKind == JsonValueKind.True)
                    throw new InvalidDataException("Cloud history has duplicate or unsafe paths; handoff was refused.");
                string first = path.Split('/')[0];
                bool isDirectory = item.TryGetProperty("IsDir", out var dir) && dir.ValueKind == JsonValueKind.True;
                if (first.Length == 10 && first.All(char.IsAsciiDigit) && long.TryParse(first, out long number))
                {
                    HighWater = Math.Max(HighWater, number);
                    var attempt = attempts.GetValueOrDefault(number) ?? new(number, null, 0, true);
                    var modified = item.TryGetProperty("ModTime", out var stamp) && stamp.ValueKind == JsonValueKind.String
                        && stamp.TryGetDateTimeOffset(out var at) && at.Year > 2000 ? at : (DateTimeOffset?)null;
                    long bytes = !isDirectory && item.TryGetProperty("Size", out var size) && size.ValueKind == JsonValueKind.Number
                        && size.TryGetInt64(out long length) ? Math.Max(0, length) : 0;
                    attempts[number] = attempt with
                    {
                        LatestWrite = attempt.LatestWrite is { } old && (modified is null || old > modified) ? old : modified,
                        Bytes = attempt.Bytes + bytes,
                        ContainsOnlyGenerationFiles = attempt.ContainsOnlyGenerationFiles
                            && (isDirectory ? path == first : IsGenerationFile(path, out _)),
                    };
                }
                if (!isDirectory)
                {
                    string? checksum = null;
                    if (item.TryGetProperty("Hashes", out var checksums))
                        foreach (var hash in checksums.EnumerateObject())
                            if (hash.Name.Equals("md5", StringComparison.OrdinalIgnoreCase)) checksum = hash.Value.GetString();
                    files.Add(path, checksum);
                }
            }
            remoteAttempts = attempts.Values.OrderBy(a => a.Number).ToArray();
            return files;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("The provider returned an unreadable cloud history listing."); }
    }

    internal static CloudCoordination For(MedalSyncConfig config, Action? requireTime = null) => new(config, CloudJournalProvider.For(config, requireTime),
        (arguments, check) => new RcloneRunner(config.ExpandedRclonePath).Run(arguments,
            TimeSpan.FromMinutes(config.Replication!.TimeoutMinutes), requireTime + check, captureOutput: true, lowPriority: check is not null));

    private string Run(string command, Action? check, params string[] rest) => run([command, .. rest, "--config",
        Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!), "--retries", "1", "--low-level-retries", "2", "--contimeout", "10s", "--timeout", "60s"], check);

    private StorageActionRequiredException StorageRefusal(RcloneFailedException cause) => new(
        RcloneFailure.Describe(cause, "Cloud coordination storage", config.Remote,
            Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!)), cause);

    private string StorageFolder(bool refresh = false)
    {
        if (!refresh && storageFolderId is not null) return storageFolderId;
        string listing;
        try { listing = Run("lsjson", null, config.Remote, "--stat", "--no-mimetype"); }
        catch (RcloneFailedException ex) when (!RcloneFailure.IsTransient(ex))
        {
            throw StorageRefusal(ex);
        }
        using var json = JsonDocument.Parse(listing);
        if (!json.RootElement.TryGetProperty("IsDir", out var isDir) || !isDir.GetBoolean()
            || !json.RootElement.TryGetProperty("ID", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidDataException("The cloud provider did not identify the dedicated storage folder.");
        return storageFolderId = id.GetString()!;
    }

    public CloudJournalVersion Read(bool refreshStorage = false)
    {
        var version = api.Read();
        var value = version.Value;
        if (value.Identity.LibraryId != config.LibraryId || value.Identity.AccountId != config.AccountId)
            throw new IncompatibleLibraryException("The cloud journal belongs to a different library or Medal account.");
        if (value.StorageFolderId != StorageFolder(refreshStorage) || !value.CaptureFolder.Equals(config.CaptureFolder, StringComparison.OrdinalIgnoreCase))
            throw new IncompatibleLibraryException("The cloud journal uses a different storage folder or capture path. Use the same cloud folder and capture path on every PC.");
        return version;
    }

    public OwnershipRecord ReadJournal()
    {
        var value = Read().Value;
        RequireHead(value);
        return value.Journal;
    }

    private string ManifestHash(long generation) => Hashing.Sha256File(Path.Combine(GenerationsLocal, GenerationStore.Name(generation), "manifest.json"));

    private void RequireHead(CloudJournal value)
    {
        var journal = value.Journal;
        // Download once for each newly observed head. Never infer Ready from this cache.
        if (!Directory.Exists(Path.Combine(GenerationsLocal, GenerationStore.Name(journal.CommittedGeneration)))
            || (File.Exists(RetentionLocal) ? Hashing.Sha256File(RetentionLocal) : null) != value.RetentionHash)
        {
            if (downloadRefusal is { } refused && refused.Generation == journal.CommittedGeneration
                && refused.Hash == value.HeadManifestHash && Now() < refused.Until) throw refused.Error;
            try { Download(); }
            catch (RcloneFailedException ex) when (!RcloneFailure.IsTransient(ex))
            {
                var refusal = StorageRefusal(ex);
                downloadRefusal = (journal.CommittedGeneration, value.HeadManifestHash, refusal, Now() + TimeSpan.FromMinutes(1));
                throw refusal;
            }
            catch (Exception ex) when (ex is InvalidDataException or StorageActionRequiredException)
            {
                // A status poll still reads the authoritative journal, but retries a refused head at most once a minute.
                downloadRefusal = (journal.CommittedGeneration, value.HeadManifestHash, ex, Now() + TimeSpan.FromMinutes(1));
                throw;
            }
        }
        if (!ManifestHash(journal.CommittedGeneration).Equals(value.HeadManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local cloud generation differs from the committed cloud manifest. Preserve this cache and unpublished local work; recover with a fresh history cache before launching.");
    }

    internal void RefreshGenerationNumbers(Action? check) => InspectGenerations(check: check);

    internal IEnumerable<CloudGenerationAttempt> IncompleteAttempts(GenerationStore store)
    {
        var retention = store.ReadRetention();
        return remoteAttempts.Where(attempt => !store.Exists(attempt.Number) && retention?.Find(attempt.Number) is null);
    }

    public void Download()
    {
        RequireOrdinaryCache();
        Directory.CreateDirectory(config.LibraryCoordinationDirectory);
        // Status, history and launch can share this cache. Never clean another process's in-flight download.
        using var gate = new FileStream(Path.Combine(config.LibraryCoordinationDirectory, ".download.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (string leftover in Directory.EnumerateDirectories(config.LibraryCoordinationDirectory, ".download-*"))
            if (Guid.TryParseExact(Path.GetFileName(leftover)[10..], "N", out _)) RemoveDownload(leftover);
        var remote = InspectGenerations(hashes: true);
        var head = Read().Value;
        long committed = head.Journal.CommittedGeneration;
        byte[]? retentionBytes = head.RetentionHash is { } hash ? ReadRetentionBytes(hash) : null;
        var retention = retentionBytes is null ? null : Json.Deserialize<GenerationRetention>(retentionBytes, "Cloud retention record");
        if (retention is not null && (retention.Format != GenerationRetention.CurrentFormat
            || retention.HighWater < 0 || retention.Ancestry is null || retention.Pruned is null
            || retention.Pruned.Any(p => p.Generation < 0 || p.Generation >= retention.HighWater || p.Generation == committed)
            || retention.Pruned.Select(p => p.Generation).Distinct().Count() != retention.Pruned.Count))
            throw new InvalidDataException("The cloud retention record is incomplete or claims its committed head.");
        var pruned = retention?.Pruned.Select(p => p.Generation).ToHashSet() ?? [];
        Directory.CreateDirectory(GenerationsLocal);
        string staging = Path.Combine(config.LibraryCoordinationDirectory, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var cached = new GenerationStore(GenerationsLocal, config.LibraryId, config.AccountId);
            var cachedNumbers = cached.Numbered().ToHashSet();
            var keptCache = cachedNumbers.Except(pruned).ToHashSet();
            foreach (long generation in keptCache)
            {
                RequireOrdinaryGeneration(Path.Combine(GenerationsLocal, GenerationStore.Name(generation)));
                // Drive supplies MD5 in its listing. Compare cached bytes without transferring their payloads again.
                VerifyRemoteGeneration(generation, GenerationsLocal, remote, requireComplete: false);
            }
            // An exact file list avoids both filter-order surprises and command-line limits as history grows.
            var missing = remote.Keys.Where(path => path.Length > 11 && path[10] == '/' && path[..10].All(char.IsAsciiDigit)
                && GenerationFiles.Contains(path[11..])).Where(path =>
                {
                    long number = long.Parse(path[..10], System.Globalization.CultureInfo.InvariantCulture);
                    return !cachedNumbers.Contains(number) && !pruned.Contains(number);
                }).ToArray();
            if (missing.Length > 0)
            {
                string files = Path.Combine(staging, ".files");
                File.WriteAllLines(files, missing);
                Run("copy", null, GenerationsRemote, staging, "--immutable", "--checksum", "--files-from-raw", files);
            }
            if (!api.HasMd5)
            {
                // Providers without Drive's MD5 listing require byte-for-byte comparison of their history files.
                VerifyHistoryFiles(GenerationsLocal, remote.Keys.Where(path => IsGenerationFile(path, out long number) && keptCache.Contains(number)), staging);
                VerifyHistoryFiles(staging, missing, staging);
            }
            var downloaded = new GenerationStore(staging, config.LibraryId, config.AccountId);
            string HeadRoot(long generation) => cached.Exists(generation) ? GenerationsLocal : staging;
            if (!Hashing.Sha256File(Path.Combine(HeadRoot(committed), GenerationStore.Name(committed), "manifest.json")).Equals(head.HeadManifestHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded cloud head does not match its authoritative journal. Ownership and all local files are retained.");
            // Follow the committed chain across both stores, loading every payload before making new folders visible.
            var required = new HashSet<long>();
            long previous = long.MaxValue;
            for (long? number = committed; number is { } generation;)
            {
                if (generation >= previous) throw new InvalidDataException("Cloud history contains a later parent generation.");
                previous = generation;
                if (pruned.Contains(generation))
                {
                    var (known, parent) = retention!.ParentOf(generation);
                    if (!known || retention.Find(generation)?.Committed != true)
                        throw new InvalidDataException("The cloud retention record cannot prove committed ancestry.");
                    number = parent;
                    continue;
                }
                VerifyRemoteGeneration(generation, HeadRoot(generation), remote, requireComplete: true);
                var content = (cached.Exists(generation) ? cached : downloaded).Read(generation);
                required.Add(generation);
                number = content.Manifest.ParentGeneration;
            }
            foreach (long generation in downloaded.Numbered())
            {
                try { _ = downloaded.Read(generation); }
                catch (Exception ex) when (!required.Contains(generation) && ex is InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
                { continue; } // Incomplete abandoned uploads reserve a number, but never become cached generations.
                Directory.Move(Path.Combine(staging, GenerationStore.Name(generation)), Path.Combine(GenerationsLocal, GenerationStore.Name(generation)));
            }
            if (retentionBytes is not null)
            {
                foreach (long generation in cachedNumbers.Intersect(pruned))
                    RemoveDownload(Path.Combine(GenerationsLocal, GenerationStore.Name(generation)));
                AtomicFile.Replace(RetentionLocal, retentionBytes);
                HighWater = Math.Max(HighWater, retention!.HighWater);
            }
            else if (File.Exists(RetentionLocal))
                throw new InvalidDataException("The local history cache contains a retention record the cloud journal does not recognize.");
            downloadRefusal = null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException("The committed cloud history is missing a generation or payload. Ownership and all local files are retained.", ex);
        }
        finally { RemoveDownload(staging); }
    }

    private static bool IsGenerationFile(string path, out long number)
    {
        number = -1;
        return path.Length > 11 && path[10] == '/' && path[..10].All(char.IsAsciiDigit) && GenerationFiles.Contains(path[11..])
            && long.TryParse(path[..10], System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private void VerifyHistoryFiles(string root, IEnumerable<string> paths, string scratch)
    {
        string[] names = paths.ToArray();
        if (names.Length == 0) return;
        string list = Path.Combine(scratch, ".check-files");
        File.WriteAllLines(list, names);
        Run("check", null, root, GenerationsRemote, "--download", "--one-way", "--files-from-raw", list);
    }

    private byte[] ReadRetentionBytes(string hash)
    {
        var bytes = Encoding.UTF8.GetBytes(Run("cat", null, RetentionRemote(hash)));
        if (Hashing.Sha256Hex(bytes) != hash) throw new InvalidDataException("The cloud retention record differs from the journal.");
        return bytes;
    }

    internal void PublishRetention(GenerationRetention record, OwnershipRecord held)
    {
        string hash = UploadRetention(record);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var current = Read(refreshStorage: true);
            if (current.Value.Journal != held) throw new OwnershipConflictException("The retention lease changed. Nothing further was removed.");
            if (current.Value.RetentionHash == hash || api.CompareExchange(current, current.Value with { RetentionHash = hash })) return;
        }
        throw new StorageBusyException("The cloud journal is busy. Retry the same retention review.");
    }

    private string UploadRetention(GenerationRetention record)
    {
        byte[] bytes = Json.Serialize(record);
        string hash = Hashing.Sha256Hex(bytes);
        string scratch = Path.Combine(Path.GetTempPath(), "medal-sync-retention-" + Guid.NewGuid().ToString("N"));
        AtomicFile.CreateNew(scratch, bytes);
        try
        {
            Run("copyto", null, scratch, RetentionRemote(hash), "--immutable", "--checksum");
            _ = ReadRetentionBytes(hash);
            return hash;
        }
        finally { File.Delete(scratch); }
    }

    internal void RemoveRetainedGeneration(long generation, Action guard)
    {
        string name = GenerationStore.Name(generation);
        var files = InspectGenerations().Keys.Where(path => path.StartsWith(name + "/", StringComparison.Ordinal)).ToArray();
        if (remoteAttempts.FirstOrDefault(attempt => attempt.Number == generation)?.ContainsOnlyGenerationFiles == false
            || files.Any(path => !GenerationFiles.Contains(path[(name.Length + 1)..])))
            throw new UnsafeOperationException($"Cloud generation {generation} contains unexpected files. Retention stopped.");
        guard();
        try { Run("purge", null, GenerationsRemote + "/" + name); }
        catch (RcloneFailedException ex) when (ex.ExitCode == 3) { } // Already removed by this review.
    }

    private void VerifyRemoteGeneration(long generation, string root, Dictionary<string, string?> remote, bool requireComplete)
    {
        foreach (string file in GenerationFiles)
        {
            string path = GenerationStore.Name(generation) + "/" + file;
            if (!remote.TryGetValue(path, out string? hash))
            {
                if (!requireComplete) continue;
                throw new InvalidDataException("A required cloud history generation file is missing. Ownership and all local files are retained.");
            }
            if (!api.HasMd5) continue;
            if (hash is null || hash.Length != 32 || !hash.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Cloud history is missing a generation file or its Drive checksum. Ownership and all local files are retained.");
            using var stream = File.OpenRead(Path.Combine(root, GenerationStore.Name(generation), file));
            if (!Convert.ToHexString(MD5.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A cached cloud generation differs from Google Drive. Preserve the cache and review the library history.");
        }
    }

    private static void RequireOrdinaryGeneration(string directory)
    {
        if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)
            || Directory.EnumerateFileSystemEntries(directory).Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
            throw new UnsafeOperationException("The cloud generation cache cannot follow a link.");
    }

    private static void RemoveDownload(string directory)
    {
        // Only our scratch directories are removed. Refuse links at every level before recursive deletion.
        RequireOrdinaryGeneration(directory);
        foreach (string child in Directory.EnumerateDirectories(directory)) RemoveDownload(child);
        foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }

    private void RequireOrdinaryCache()
    {
        for (DirectoryInfo? path = new(config.LibraryCoordinationDirectory); path is not null; path = path.Parent)
            if (path.Exists && path.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnsafeOperationException("The cloud history cache cannot follow a directory link.");
        if (Directory.Exists(GenerationsLocal) && new DirectoryInfo(GenerationsLocal).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnsafeOperationException("The cloud generation cache cannot be a directory link.");
    }

    public void Upload(long generation, Action? check = null)
    {
        RequireOrdinaryCache();
        UploadFrom(new GenerationStore(GenerationsLocal, config.LibraryId, config.AccountId), generation, check);
    }

    internal void UploadFrom(GenerationStore source, long generation, Action? check = null)
    {
        string folder = Path.Combine(source.Directory, GenerationStore.Name(generation));
        RequireOrdinaryGeneration(folder);
        // Load verifies both payloads and the manifest before anything is uploaded.
        _ = source.Read(generation);
        string destination = GenerationsRemote + "/" + GenerationStore.Name(generation);
        InspectGenerations(allowMissing: true, check: check);
        string[] filters = ["--include", "/library.json", "--include", "/assets.json", "--include", "/manifest.json"];
        Run("copy", check, [folder, destination, "--immutable", "--checksum", .. filters]);
        InspectGenerations(check: check);
        Run("check", check, [folder, destination, "--download", "--one-way", .. filters]);
    }

    internal bool HasJournal() => api.Exists();

    internal string StorageIdentity() => StorageFolder(refresh: true);

    internal void StageMigration(LibraryIdentity identity, OwnershipRecord held, string headHash, GenerationRetention? retention)
    {
        string folder = StorageIdentity();
        var planned = new CloudJournal(1, identity, held, [], folder, config.CaptureFolder, headHash,
            retention is null ? null : UploadRetention(retention), held.OperationDigest);
        if (!api.Exists()) api.Create(planned, folder);
        var observed = Read(refreshStorage: true).Value;
        if (observed.MigrationDigest != planned.MigrationDigest || observed.Journal != held
            || observed.HeadManifestHash != headHash || observed.RetentionHash != planned.RetentionHash || observed.Identity != identity)
            throw new UnsafeOperationException("The destination cloud journal does not match this migration. The source retains ownership.");
    }

    internal void ActivateMigration(OwnershipRecord source, DeviceIdentity device, string operation)
    {
        if (source.State != OwnershipState.Migrated)
            throw new InvalidOperationException("The previous journal must be retired before activating the cloud destination.");
        var target = Read(refreshStorage: true).Value;
        if (target.MigrationDigest != source.OperationDigest || target.Journal.CommittedGeneration < source.CommittedGeneration)
            throw new UnsafeOperationException("The destination journal does not match the retired source. Keep the migration review and retry.");
        if (target.Journal.State != OwnershipState.Migrating) return; // Already activated, possibly in use on another PC.
        Update(current => current == target.Journal && current.OperationDigest == source.OperationDigest
            ? Ownership.ActivateMigration(current, device, operation, DateTimeOffset.UtcNow)
            : throw new OwnershipConflictException("The staged destination changed. Retry the migration review."));
        var confirmed = Read().Value;
        if (confirmed.MigrationDigest != source.OperationDigest || confirmed.Journal == target.Journal)
            throw new StorageOfflineException("The destination did not confirm migration activation. Retry the same review.");
    }

    public OwnershipRecord Update(Func<OwnershipRecord, OwnershipRecord> transition, Action? check = null)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var current = Read(refreshStorage: true);
            RequireHead(current.Value);
            var next = transition(current.Value.Journal);
            if (ReferenceEquals(next, current.Value.Journal))
            {
                // A new process resuming BeginPublish has no in-memory high-water mark.
                if (next.State == OwnershipState.Publishing) Download();
                return next;
            }
            if (next.Revision != current.Value.Journal.Revision + 1 || next.LibraryId != config.LibraryId || next.AccountId != config.AccountId)
                throw new InvalidOperationException("A cloud journal transition must advance the revision by exactly one.");
            if (next.CommittedGeneration != current.Value.Journal.CommittedGeneration) Upload(next.CommittedGeneration, check);
            // Discover immutable attempts left by an abandoned PC before selecting the next generation number.
            if (next.State == OwnershipState.Publishing && current.Value.Journal.State != OwnershipState.Publishing) Download();
            string hash = next.CommittedGeneration == current.Value.Journal.CommittedGeneration
                ? current.Value.HeadManifestHash : ManifestHash(next.CommittedGeneration);
            check?.Invoke();
            if (!api.CompareExchange(current, current.Value with { Journal = next, HeadManifestHash = hash })) continue;
            // A later legitimate transition may already have happened; the successful CAS is the ownership proof.
            return next;
        }
        throw new StorageBusyException("Another PC is updating the cloud journal. Try again shortly.");
    }

    public void Request(HandoffRequest request, bool remove = false)
    {
        if (!CoordinationStore.IsValidId(request.Device.DeviceId) || !CoordinationStore.IsValidId(request.RequestId))
            throw new ArgumentException("Invalid handoff request identity.");
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var current = Read();
            var now = Now();
            var requests = current.Value.Requests.Where(r => r.Device.DeviceId != request.Device.DeviceId && r.IsFreshInCloud(now)).ToList();
            if (!remove) requests.Add(request);
            if (requests.Count > 32) throw new StorageBusyException("Too many PCs are waiting for this library.");
            if (api.CompareExchange(current, current.Value with { Requests = requests })) return;
        }
        throw new StorageBusyException("Another PC is updating switch requests. Try again shortly.");
    }

    /// <summary>Removes <paramref name="request"/> only while it is still this device's current request. True when removed.</summary>
    public bool Withdraw(HandoffRequest request)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var current = Read();
            if (!current.Value.Requests.Any(r => r == request)) return false;
            var now = Now();
            var requests = current.Value.Requests.Where(r => r != request && r.IsFreshInCloud(now)).ToList();
            if (api.CompareExchange(current, current.Value with { Requests = requests })) return true;
        }
        throw new StorageBusyException("Another PC is updating switch requests. Try again shortly.");
    }

    public void Create(LibrarySnapshot snapshot, AssetManifest assets, DeviceIdentity device, string session, MedalVersions medal, DateTimeOffset now)
    {
        // The pre-generated ID makes simultaneous initialization or retry return 409, rather than create two journals.
        RequireOrdinaryCache();
        var generations = new GenerationStore(GenerationsLocal, config.LibraryId, config.AccountId);
        string[] entries = Directory.Exists(config.LibraryCoordinationDirectory)
            ? Directory.GetFileSystemEntries(config.LibraryCoordinationDirectory) : [];
        const string differs = "The retained cloud initialization differs from this PC's reviewed library. Preserve both and recover before retrying.";
        if (entries.Length != 0
            && (!entries.SequenceEqual([GenerationsLocal], StringComparer.OrdinalIgnoreCase)
                || !CoordinationStore.IsInitGenerationDirectory(GenerationsLocal)))
            throw new UnsafeOperationException(differs);

        DateTimeOffset createdAt = now;
        if (generations.Exists(0))
        {
            // Resume only this device's exact verified bootstrap. Never adopt a cached or edited library.
            var original = generations.Read(0);
            if (original.Manifest.ParentGeneration is not null || original.Manifest.Device.DeviceId != device.DeviceId
                || original.Snapshot.Digest() != snapshot.Digest()
                || !Json.Serialize(original.Assets).AsSpan().SequenceEqual(Json.Serialize(assets)))
                throw new UnsafeOperationException(differs);
            createdAt = original.Manifest.CreatedAt;
        }
        else
        {
            // Staging folders from an interrupted local publish are never committed generations.
            generations.Publish(0, null, session, device, snapshot, assets, medal, now);
        }
        var identity = new LibraryIdentity(1, config.LibraryId, config.AccountId, createdAt, device);
        Upload(0);
        api.Create(new(1, identity, Ownership.Initial(config.LibraryId, config.AccountId, identity.CreatedAt), [], StorageFolder(), config.CaptureFolder, ManifestHash(0)), StorageFolder());
    }
}

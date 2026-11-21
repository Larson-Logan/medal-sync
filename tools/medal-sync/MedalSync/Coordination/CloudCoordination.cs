using System.Text.Json;
using System.Security.Cryptography;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Util;

namespace MedalSync.Coordination;

/// <summary>Drive metadata is authoritative; local files are only an immutable generation cache.</summary>
internal sealed class CloudCoordination(MedalSyncConfig config, GoogleDriveJournal api,
    Func<IReadOnlyList<string>, string> run)
{
    private string GenerationsRemote => config.Remote.TrimEnd('/') + "/.medal-sync/" + config.LibraryId + "/generations";
    private string GenerationsLocal => Path.Combine(config.LibraryCoordinationDirectory, "generations");
    private string? storageFolderId;
    public long HighWater { get; private set; } = -1;
    private static readonly string[] GenerationFiles = ["library.json", "assets.json", "manifest.json"];
    private (long Generation, string Hash, Exception Error, DateTimeOffset Until)? downloadRefusal;
    internal bool HasHistoryRefusal => downloadRefusal is not null;
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Drive permits duplicate names. Refuse ambiguous paths before copying; reserve even incomplete attempts' numbers.</summary>
    private Dictionary<string, string?> InspectGenerations(bool allowMissing = false, bool hashes = false)
    {
        string listing;
        try { listing = Run("lsjson", [GenerationsRemote, "--recursive", "--no-mimetype", .. hashes ? new[] { "--hash-type", "MD5" } : Array.Empty<string>()]); }
        catch (RcloneFailedException ex) when (allowMissing && ex.ExitCode == 3) { return []; }
        try
        {
            using var json = JsonDocument.Parse(listing);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                string? path = item.GetProperty("Path").GetString();
                if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Split('/').Any(s => s is "" or "." or "..")
                    || !paths.Add(path) || item.TryGetProperty("IsLink", out var link) && link.ValueKind == JsonValueKind.True)
                    throw new InvalidDataException("Cloud history has duplicate or unsafe paths; handoff was refused.");
                string first = path.Split('/')[0];
                if (first.Length == 10 && first.All(char.IsAsciiDigit) && long.TryParse(first, out long number)) HighWater = Math.Max(HighWater, number);
                if (!item.TryGetProperty("IsDir", out var directory) || directory.ValueKind != JsonValueKind.True)
                {
                    string? checksum = null;
                    if (item.TryGetProperty("Hashes", out var checksums))
                        foreach (var hash in checksums.EnumerateObject())
                            if (hash.Name.Equals("md5", StringComparison.OrdinalIgnoreCase)) checksum = hash.Value.GetString();
                    files.Add(path, checksum);
                }
            }
            return files;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("Google Drive returned an unreadable cloud history listing."); }
    }

    internal static CloudCoordination For(MedalSyncConfig config, Action? requireTime = null) => new(config, GoogleDriveJournal.For(config, requireTime),
        arguments => new RcloneRunner(config.ExpandedRclonePath).Run(arguments, TimeSpan.FromMinutes(config.Replication!.TimeoutMinutes), requireTime, captureOutput: true));

    private string Run(string command, params string[] rest) => run([command, .. rest, "--config",
        Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!), "--retries", "1", "--low-level-retries", "2", "--contimeout", "10s", "--timeout", "60s"]);

    private StorageActionRequiredException StorageRefusal(RcloneFailedException cause) => new(
        RcloneFailure.Describe(cause, "Google Drive coordination storage", config.Remote,
            Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!)), cause);

    private string StorageFolder(bool refresh = false)
    {
        if (!refresh && storageFolderId is not null) return storageFolderId;
        string listing;
        try { listing = Run("lsjson", config.Remote, "--stat", "--no-mimetype"); }
        catch (RcloneFailedException ex) when (!RcloneFailure.IsTransient(ex))
        {
            throw StorageRefusal(ex);
        }
        using var json = JsonDocument.Parse(listing);
        if (!json.RootElement.TryGetProperty("IsDir", out var isDir) || !isDir.GetBoolean()
            || !json.RootElement.TryGetProperty("ID", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidDataException("Google Drive did not identify the dedicated storage folder.");
        return storageFolderId = id.GetString()!;
    }

    public CloudJournalVersion Read(bool refreshStorage = false)
    {
        var version = api.Read();
        var value = version.Value;
        if (value.Identity.LibraryId != config.LibraryId || value.Identity.AccountId != config.AccountId)
            throw new IncompatibleLibraryException("The Google Drive journal belongs to a different library or Medal account.");
        if (value.StorageFolderId != StorageFolder(refreshStorage) || !value.CaptureFolder.Equals(config.CaptureFolder, StringComparison.OrdinalIgnoreCase))
            throw new IncompatibleLibraryException("The Google Drive journal uses a different storage folder or capture path. Use the same Drive folder and capture path on every PC.");
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
        if (!Directory.Exists(Path.Combine(GenerationsLocal, GenerationStore.Name(journal.CommittedGeneration))))
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
            throw new InvalidDataException("The local cloud generation differs from the committed Drive manifest. Preserve this cache and unpublished local work; recover with a fresh history cache before launching.");
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
        Directory.CreateDirectory(GenerationsLocal);
        string staging = Path.Combine(config.LibraryCoordinationDirectory, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var cached = new GenerationStore(GenerationsLocal, config.LibraryId, config.AccountId);
            var cachedNumbers = cached.Numbered().ToHashSet();
            foreach (long generation in cachedNumbers)
            {
                RequireOrdinaryGeneration(Path.Combine(GenerationsLocal, GenerationStore.Name(generation)));
                // Drive supplies MD5 in its listing. Compare cached bytes without transferring their payloads again.
                VerifyRemoteGeneration(generation, GenerationsLocal, remote, requireComplete: false);
            }
            // An exact file list avoids both filter-order surprises and command-line limits as history grows.
            var missing = remote.Keys.Where(path => path.Length > 11 && path[10] == '/' && path[..10].All(char.IsAsciiDigit)
                && GenerationFiles.Contains(path[11..]) && !cachedNumbers.Contains(long.Parse(path[..10], System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            if (missing.Length > 0)
            {
                string files = Path.Combine(staging, ".files");
                File.WriteAllLines(files, missing);
                Run("copy", GenerationsRemote, staging, "--immutable", "--checksum", "--files-from-raw", files);
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
            downloadRefusal = null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException("The committed cloud history is missing a generation or payload. Ownership and all local files are retained.", ex);
        }
        finally { RemoveDownload(staging); }
    }

    private static void VerifyRemoteGeneration(long generation, string root, Dictionary<string, string?> remote, bool requireComplete)
    {
        foreach (string file in GenerationFiles)
        {
            string path = GenerationStore.Name(generation) + "/" + file;
            if (!remote.TryGetValue(path, out string? hash) && !requireComplete) continue;
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

    public void Upload(long generation)
    {
        RequireOrdinaryCache();
        string folder = Path.Combine(GenerationsLocal, GenerationStore.Name(generation));
        // Load verifies both payloads and the manifest before anything is uploaded.
        _ = new GenerationStore(GenerationsLocal, config.LibraryId, config.AccountId).Read(generation);
        string destination = GenerationsRemote + "/" + GenerationStore.Name(generation);
        InspectGenerations(allowMissing: true);
        string[] filters = ["--include", "/library.json", "--include", "/assets.json", "--include", "/manifest.json"];
        Run("copy", [folder, destination, "--immutable", "--checksum", .. filters]);
        InspectGenerations();
        Run("check", [folder, destination, "--download", "--one-way", .. filters]);
    }

    public OwnershipRecord Update(Func<OwnershipRecord, OwnershipRecord> transition)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var current = Read(refreshStorage: true);
            RequireHead(current.Value);
            var next = transition(current.Value.Journal);
            if (ReferenceEquals(next, current.Value.Journal)) return next;
            if (next.Revision != current.Value.Journal.Revision + 1 || next.LibraryId != config.LibraryId || next.AccountId != config.AccountId)
                throw new InvalidOperationException("A cloud journal transition must advance the revision by exactly one.");
            if (next.CommittedGeneration != current.Value.Journal.CommittedGeneration) Upload(next.CommittedGeneration);
            // Discover immutable attempts left by an abandoned PC before selecting the next generation number.
            if (next.State == OwnershipState.Publishing && current.Value.Journal.State != OwnershipState.Publishing) Download();
            string hash = next.CommittedGeneration == current.Value.Journal.CommittedGeneration
                ? current.Value.HeadManifestHash : ManifestHash(next.CommittedGeneration);
            if (!api.CompareExchange(current, current.Value with { Journal = next, HeadManifestHash = hash })) continue;
            // A later legitimate transition may already have happened; the successful CAS is the ownership proof.
            return next;
        }
        throw new StorageBusyException("Another PC is updating the Google Drive journal. Try again shortly.");
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
        LibraryIdentity identity;
        if (Directory.Exists(config.LibraryCoordinationDirectory) && Directory.EnumerateFileSystemEntries(config.LibraryCoordinationDirectory).Any())
        {
            // Resume only this device's exact verified bootstrap. Never adopt a cached or edited library.
            var original = generations.Read(0);
            if (!Directory.EnumerateFileSystemEntries(config.LibraryCoordinationDirectory).SequenceEqual([GenerationsLocal], StringComparer.OrdinalIgnoreCase)
                || !generations.Numbered().SequenceEqual([0L]) || original.Manifest.Device.DeviceId != device.DeviceId || original.Snapshot.Digest() != snapshot.Digest()
                || !Json.Serialize(original.Assets).AsSpan().SequenceEqual(Json.Serialize(assets)))
                throw new UnsafeOperationException("The retained cloud initialization differs from this PC's reviewed library. Preserve both and recover before retrying.");
            identity = new(1, config.LibraryId, config.AccountId, original.Manifest.CreatedAt, device);
        }
        else
        {
            identity = new(1, config.LibraryId, config.AccountId, now, device);
            generations.Publish(0, null, session, device, snapshot, assets, medal, now);
        }
        Upload(0);
        api.Create(new(1, identity, Ownership.Initial(config.LibraryId, config.AccountId, identity.CreatedAt), [], StorageFolder(), config.CaptureFolder, ManifestHash(0)), StorageFolder());
    }
}

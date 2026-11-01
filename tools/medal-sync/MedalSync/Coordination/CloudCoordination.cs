using System.Text.Json;
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

    /// <summary>Drive permits duplicate names. Refuse ambiguous paths before copying; reserve even incomplete attempts' numbers.</summary>
    private void InspectGenerations(bool allowMissing = false)
    {
        string listing;
        try { listing = Run("lsjson", GenerationsRemote, "--recursive", "--no-mimetype"); }
        catch (RcloneFailedException ex) when (allowMissing && ex.ExitCode == 3) { return; }
        try
        {
            using var json = JsonDocument.Parse(listing);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                string? path = item.GetProperty("Path").GetString();
                if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Split('/').Any(s => s is "" or "." or "..")
                    || !paths.Add(path) || item.TryGetProperty("IsLink", out var link) && link.ValueKind == JsonValueKind.True)
                    throw new InvalidDataException("Cloud history has duplicate or unsafe paths; handoff was refused.");
                string first = path.Split('/')[0];
                if (first.Length == 10 && first.All(char.IsAsciiDigit) && long.TryParse(first, out long number)) HighWater = Math.Max(HighWater, number);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("Google Drive returned an unreadable cloud history listing."); }
    }

    internal static CloudCoordination For(MedalSyncConfig config, Action? requireTime = null) => new(config, GoogleDriveJournal.For(config, requireTime),
        arguments => new RcloneRunner(config.ExpandedRclonePath).Run(arguments, TimeSpan.FromMinutes(config.Replication!.TimeoutMinutes), requireTime, captureOutput: true));

    private string Run(string command, params string[] rest) => run([command, .. rest, "--config",
        Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!), "--retries", "1", "--low-level-retries", "2", "--contimeout", "10s", "--timeout", "60s"]);

    private string StorageFolder(bool refresh = false)
    {
        if (!refresh && storageFolderId is not null) return storageFolderId;
        using var json = JsonDocument.Parse(Run("lsjson", config.Remote, "--stat", "--no-mimetype"));
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
        if (!Directory.Exists(Path.Combine(GenerationsLocal, GenerationStore.Name(journal.CommittedGeneration)))) Download();
        if (!ManifestHash(journal.CommittedGeneration).Equals(value.HeadManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local cloud generation differs from the committed Drive manifest. Preserve this cache and unpublished local work; recover with a fresh history cache before launching.");
    }

    public void Download()
    {
        RequireOrdinaryCache();
        InspectGenerations();
        var head = Read().Value;
        long committed = head.Journal.CommittedGeneration;
        Directory.CreateDirectory(GenerationsLocal);
        string staging = Path.Combine(config.LibraryCoordinationDirectory, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        // A failed download never makes a partial numbered directory visible to readers.
        Run("copy", GenerationsRemote, staging, "--immutable", "--checksum",
            "--include", "/*/library.json", "--include", "/*/assets.json", "--include", "/*/manifest.json");
        var downloaded = new GenerationStore(staging, config.LibraryId, config.AccountId);
        if (!Hashing.Sha256File(Path.Combine(staging, GenerationStore.Name(committed), "manifest.json")).Equals(head.HeadManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded cloud head does not match its authoritative journal. Ownership and all local files are retained.");
        var required = downloaded.Chain(committed).Select(entry => entry.Generation).ToHashSet();
        foreach (long generation in required) _ = downloaded.Read(generation);
        foreach (long generation in downloaded.Numbered())
        {
            try { _ = downloaded.Read(generation); }
            catch (Exception ex) when (!required.Contains(generation) && ex is InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
            {
                // An abandoned upload may have only some of its files in Drive. Keep evidence but never reuse its number.
                continue;
            }
            string source = Path.Combine(staging, GenerationStore.Name(generation));
            string destination = Path.Combine(GenerationsLocal, GenerationStore.Name(generation));
            if (!Directory.Exists(destination)) Directory.Move(source, destination);
            else
            {
                foreach (string file in new[] { "library.json", "assets.json", "manifest.json" })
                    if (!File.ReadAllBytes(Path.Combine(source, file)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(destination, file))))
                        throw new InvalidDataException("A cached cloud generation differs from Google Drive. Preserve both copies and review the library history.");
                foreach (string file in Directory.GetFiles(source)) File.Delete(file);
                Directory.Delete(source);
            }
        }
        if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
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
            var requests = current.Value.Requests.Where(r => r.Device.DeviceId != request.Device.DeviceId
                && DateTimeOffset.UtcNow - r.RequestedAt < HandoffRequest.Lifetime).ToList();
            if (!remove) requests.Add(request);
            if (requests.Count > 32) throw new StorageBusyException("Too many PCs are waiting for this library.");
            if (api.CompareExchange(current, current.Value with { Requests = requests })) return;
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
                || !generations.Numbered().SequenceEqual([0L]) || original.Manifest.Device != device || original.Snapshot.Digest() != snapshot.Digest()
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

using System.Globalization;
using System.Text.RegularExpressions;
using MedalSync.Library;
using MedalSync.Util;

namespace MedalSync.Coordination;

internal sealed record MedalVersions(string? App, string? Recorder);

internal sealed record GenerationFile(string Sha256, long Bytes);

internal sealed record AssetEntry(string Path, long Length, DateTime LastWriteUtc);

internal sealed record MissingAsset(string ContentId, string Field, string Path);

/// <summary>Files a generation's library points at, as seen when it was published.</summary>
internal sealed record AssetManifest(int Format, IReadOnlyList<AssetEntry> Present, IReadOnlyList<MissingAsset> Missing)
{
    public const int CurrentFormat = 1;

    public static AssetManifest From(AssetReport report)
    {
        var present = report.Items
            .Where(i => i.Problem == AssetProblem.None)
            .GroupBy(i => i.Reference.Path.ToUpperInvariant(), StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(i => new AssetEntry(i.Reference.Path, i.Length ?? 0, i.LastWriteUtc ?? default))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missing = report.Items
            .Where(i => i.Problem != AssetProblem.None)
            .Select(i => new MissingAsset(i.Reference.ContentId, i.Reference.Field, i.Reference.Path))
            .OrderBy(m => m.ContentId, StringComparer.Ordinal).ThenBy(m => m.Field, StringComparer.Ordinal)
            .ToArray();
        return new AssetManifest(CurrentFormat, present, missing);
    }
}

internal sealed record GenerationManifest(
    int Format,
    string LibraryId,
    string AccountId,
    long Generation,
    long? ParentGeneration,
    string SessionId,
    DeviceIdentity Device,
    DateTimeOffset CreatedAt,
    MedalVersions Medal,
    long SchemaVersion,
    string SchemaFingerprint,
    string SnapshotDigest,
    IReadOnlyDictionary<string, int> RowCounts,
    IReadOnlyDictionary<string, GenerationFile> Files,
    string ToolVersion)
{
    public const int CurrentFormat = 1;
}

internal sealed record GenerationContent(GenerationManifest Manifest, LibrarySnapshot Snapshot, AssetManifest Assets);

/// <summary>
/// Immutable, numbered library generations. Each is written into a staging
/// folder, verified, then renamed into place in one step, so a numbered folder
/// is always complete; interrupted staging folders are ignored.
/// </summary>
internal sealed partial class GenerationStore(string directory, string libraryId, string accountId)
{
    private const string LibraryFile = "library.json";
    private const string AssetsFile = "assets.json";
    private const string ManifestFile = "manifest.json";

    public string Directory { get; } = directory;

    public static string Name(long generation) => generation.ToString("D10", CultureInfo.InvariantCulture);

    public bool Exists(long generation) => System.IO.Directory.Exists(Path.Combine(Directory, Name(generation)));

    /// <summary>
    /// Every complete generation folder, including any an abandoned session left
    /// behind. Only the journal says which generation is committed.
    /// </summary>
    public IReadOnlyList<long> Numbered() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateDirectories(Directory)
                .Select(Path.GetFileName)
                .Where(n => n is not null && NumberedName().IsMatch(n))
                .Select(n => long.Parse(n!, CultureInfo.InvariantCulture))
                .Order()
                .ToArray()
            : [];

    public GenerationManifest Publish(long generation, long? parent, string session, DeviceIdentity device, LibrarySnapshot snapshot,
        AssetManifest assets, MedalVersions medal, DateTimeOffset now)
    {
        if (snapshot.AccountId != accountId) throw new IncompatibleLibraryException($"The snapshot is for account {snapshot.AccountId}, not {accountId}.");
        string final = Path.Combine(Directory, Name(generation));
        if (System.IO.Directory.Exists(final)) throw new OwnershipConflictException($"Generation {generation} already exists.");
        System.IO.Directory.CreateDirectory(Directory);
        string staging = Path.Combine(Directory, $".staging-{Name(generation)}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(staging);

        byte[] library = snapshot.ToCanonicalJson();
        byte[] assetBytes = Json.Serialize(assets);
        var manifest = new GenerationManifest(GenerationManifest.CurrentFormat, libraryId, accountId, generation, parent, session, device, now,
            medal, snapshot.SchemaVersion, snapshot.SchemaFingerprint, snapshot.Digest(),
            MedalSchema.LibraryTables.ToDictionary(t => t, snapshot.RowCount, StringComparer.Ordinal),
            new Dictionary<string, GenerationFile>(StringComparer.Ordinal)
            {
                [LibraryFile] = new(Hashing.Sha256Hex(library), library.LongLength),
                [AssetsFile] = new(Hashing.Sha256Hex(assetBytes), assetBytes.LongLength),
            },
            ToolInfo.Version);
        AtomicFile.CreateNew(Path.Combine(staging, LibraryFile), library);
        AtomicFile.CreateNew(Path.Combine(staging, AssetsFile), assetBytes);
        AtomicFile.CreateNew(Path.Combine(staging, ManifestFile), Json.Serialize(manifest));
        // Prove the staged bytes before they become visible.
        Load(staging, generation);
        System.IO.Directory.Move(staging, final);
        return manifest;
    }

    public GenerationContent Read(long generation)
    {
        string folder = Path.Combine(Directory, Name(generation));
        if (!System.IO.Directory.Exists(folder)) throw new InvalidDataException($"Generation {generation} does not exist.");
        return Load(folder, generation);
    }

    /// <summary>Reads and checks only a generation's manifest, without loading its library.</summary>
    public GenerationManifest ReadManifest(long generation)
    {
        string folder = Path.Combine(Directory, Name(generation));
        if (!System.IO.Directory.Exists(folder)) throw new InvalidDataException($"Generation {generation} does not exist.");
        return LoadManifest(folder, generation);
    }

    private GenerationManifest LoadManifest(string folder, long generation)
    {
        var manifest = Json.ReadFile<GenerationManifest>(Path.Combine(folder, ManifestFile), "Generation manifest");
        if (manifest.Format != GenerationManifest.CurrentFormat) throw new InvalidDataException($"Generation {generation} uses manifest format {manifest.Format}.");
        if (manifest.Generation != generation || manifest.LibraryId != libraryId || manifest.AccountId != accountId)
        {
            throw new InvalidDataException($"Generation {generation}'s manifest names generation {manifest.Generation} of library {manifest.LibraryId}.");
        }
        if (manifest.RowCounts is null || manifest.Files is null || manifest.Device is null || manifest.SnapshotDigest is null
            || !manifest.RowCounts.Keys.Order(StringComparer.Ordinal).SequenceEqual(MedalSchema.LibraryTables.Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException($"Generation {generation}'s manifest is incomplete.");
        }
        return manifest;
    }

    private GenerationContent Load(string folder, long generation)
    {
        var manifest = LoadManifest(folder, generation);
        byte[] library = ReadVerified(folder, LibraryFile, manifest);
        byte[] assets = ReadVerified(folder, AssetsFile, manifest);
        var snapshot = LibrarySnapshot.FromCanonicalJson(library);
        if (snapshot.Digest() != manifest.SnapshotDigest || snapshot.AccountId != accountId
            || snapshot.SchemaFingerprint != manifest.SchemaFingerprint)
        {
            throw new InvalidDataException($"Generation {generation}'s library does not match its manifest.");
        }
        foreach (var (table, count) in manifest.RowCounts)
        {
            if (snapshot.RowCount(table) != count) throw new InvalidDataException($"Generation {generation} has the wrong {table} row count.");
        }
        return new GenerationContent(manifest, snapshot, Json.Deserialize<AssetManifest>(assets, "Asset manifest"));
    }

    private static byte[] ReadVerified(string folder, string name, GenerationManifest manifest)
    {
        if (!manifest.Files.TryGetValue(name, out GenerationFile? expected)) throw new InvalidDataException($"The manifest does not list {name}.");
        byte[] bytes = File.ReadAllBytes(Path.Combine(folder, name));
        if (bytes.LongLength != expected.Bytes || Hashing.Sha256Hex(bytes) != expected.Sha256)
        {
            throw new InvalidDataException($"{name} in generation {manifest.Generation} is damaged or incomplete.");
        }
        return bytes;
    }

    [GeneratedRegex("^[0-9]{10}$")]
    private static partial Regex NumberedName();
}

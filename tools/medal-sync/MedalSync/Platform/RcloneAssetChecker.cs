using System.Text.Json;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>
/// Primary storage on a named rclone remote (cloud or personal server). Presence comes from
/// one recursive listing per check; byte verification happens in <see cref="StorageReplication"/>.
/// </summary>
internal sealed class RcloneAssetChecker(MedalSyncConfig config, LocalState state, Func<IReadOnlyList<string>, string>? run = null) : IAssetChecker
{
    private Dictionary<string, long>? _listing;

    private string Credentials => Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!);

    private string Run(IReadOnlyList<string> arguments)
    {
        if (run is not null) return run(arguments);
        if (!File.Exists(Credentials)) throw new IOException("The configured rclone credential file is missing; configure the provider before retrying.");
        return new RcloneRunner(config.ExpandedRclonePath).Run(arguments, TimeSpan.FromMinutes(5), captureOutput: true);
    }

    private IReadOnlyList<string> Common(string command, params string[] rest) =>
        [command, .. rest, "--config", Credentials, "--retries", "1", "--low-level-retries", "2", "--contimeout", "10s", "--timeout", "60s"];

    public void RequireServer()
    {
        string text;
        try { text = Run(Common("cat", config.PrimaryCapture + "/" + MedalSyncConfig.StorageMarker)); }
        catch (RcloneFailedException ex) when (ex.ExitCode is 3 or 4)
        {
            throw new UnsafeOperationException($"Primary storage {config.PrimaryCapture} has no Medal Sync marker. Run 'medal-sync storage init-primary' to prepare it.");
        }
        catch (IOException ex) when (ex is not StorageOfflineException)
        {
            throw new StorageOfflineException($"Primary storage ({config.PrimaryCapture}) cannot be reached right now.", ex);
        }
        if (text.Trim() != config.LibraryId) throw new UnsafeOperationException($"Primary storage {config.PrimaryCapture} does not identify library {config.LibraryId}.");
        _listing = List();
    }

    public AssetReport ThroughMount(LibrarySnapshot snapshot) =>
        AssetInventory.Check(AssetInventory.Collect(snapshot), [config.Capture], new FileSystemProbe());

    public AssetReport OnServer(LibrarySnapshot snapshot)
    {
        _listing = List();
        return AssetInventory.Check(AssetInventory.Collect(snapshot), [config.Capture], new ListingProbe(config.Capture, _listing));
    }

    public bool ExistsOnServer(string capturePath) =>
        WindowsPath.TryParse(capturePath, out var path, out _) && path.IsWithin(config.Capture)
        && new ListingProbe(config.Capture, _listing ??= List()).Probe(path, config.Capture).Exists;

    /// <summary>Creates the identity marker on an empty primary folder, or verifies an existing one.</summary>
    public void Initialize(bool allowExistingMedia)
    {
        try
        {
            RequireServer();
            return;
        }
        catch (UnsafeOperationException) when (!MarkerPresent()) { }
        if (!allowExistingMedia && List().Count > 0) throw new UnsafeOperationException($"Primary storage {config.PrimaryCapture} already contains files but no marker; choose an empty dedicated folder.");
        Directory.CreateDirectory(state.Root);
        string local = Path.Combine(state.Root, "primary-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            AtomicFile.CreateNew(local, System.Text.Encoding.UTF8.GetBytes(config.LibraryId + "\n"));
            Run(Common("copyto", local, config.PrimaryCapture + "/" + MedalSyncConfig.StorageMarker, "--immutable"));
        }
        finally { File.Delete(local); }
        RequireServer();
    }

    private bool MarkerPresent()
    {
        try { Run(Common("cat", config.PrimaryCapture + "/" + MedalSyncConfig.StorageMarker)); return true; }
        catch (RcloneFailedException ex) when (ex.ExitCode is 3 or 4) { return false; }
    }

    private Dictionary<string, long> List()
    {
        string json;
        try { json = Run(Common("lsjson", config.PrimaryCapture, "--recursive", "--files-only", "--no-mimetype")); }
        catch (RcloneFailedException ex) when (ex.ExitCode == 3) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (IOException ex) when (ex is not StorageOfflineException)
        {
            throw new StorageOfflineException($"Primary storage ({config.PrimaryCapture}) cannot be listed right now.", ex);
        }
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray())
                if (item.TryGetProperty("Path", out var path) && path.ValueKind == JsonValueKind.String
                    && !(item.TryGetProperty("IsDir", out var dir) && dir.ValueKind == JsonValueKind.True))
                    files[path.GetString()!] = item.TryGetProperty("Size", out var size) && size.TryGetInt64(out long bytes) ? bytes : 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new StorageOfflineException("rclone returned an unreadable listing for primary storage; nothing was assumed missing.", ex);
        }
        return files;
    }

    /// <summary>Answers for capture-folder paths (M:\Clips\...) from a remote listing. Remotes have no reparse points.</summary>
    private sealed class ListingProbe(WindowsPath capture, IReadOnlyDictionary<string, long> files) : IFileProbe
    {
        public FileProbeResult Probe(WindowsPath path, WindowsPath root)
        {
            string relative = string.Join('/', path.Segments.Skip(capture.Segments.Count));
            return files.TryGetValue(relative, out long length)
                ? new(true, false, length, DateTime.UnixEpoch, false) : new(false, false, 0, DateTime.UnixEpoch, false);
        }
    }
}

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
/// <param name="requireTime">Checked while rclone runs, so a Windows shutdown budget stops slow provider checks.</param>
internal sealed class RcloneAssetChecker(MedalSyncConfig config, LocalState state, Func<IReadOnlyList<string>, string>? run = null,
    Action? requireTime = null) : IAssetChecker
{
    private Dictionary<string, long>? _listing;
    private bool _hasEntries;

    private string Credentials => Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!);

    private string Run(IReadOnlyList<string> arguments)
    {
        requireTime?.Invoke();
        if (run is not null) return run(arguments);
        if (!File.Exists(Credentials))
            throw new StorageActionRequiredException($"The rclone credential file for primary storage {config.PrimaryCapture} is missing at {Credentials}. "
                + "Copy it to this PC from the PC where you ran 'rclone config', or create it there again.");
        return new RcloneRunner(config.ExpandedRclonePath).Run(arguments, TimeSpan.FromMinutes(5), requireTime, captureOutput: true);
    }

    /// <summary>An outage is a calm wait; a sign-in, configuration or other definite refusal names its cause instead.</summary>
    private IOException Unavailable(IOException ex, string outage) =>
        ex is RcloneFailedException failed && !RcloneFailure.IsTransient(failed)
            ? new StorageActionRequiredException(RcloneFailure.Describe(failed, $"Primary storage {config.PrimaryCapture}", config.PrimaryCapture, Credentials), ex)
            : new StorageOfflineException($"Primary storage ({config.PrimaryCapture}) {outage}.", ex);

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
        catch (IOException ex) when (ex is not (StorageOfflineException or StorageActionRequiredException))
        {
            throw Unavailable(ex, "cannot be reached right now");
        }
        if (text.Trim() != config.LibraryId) throw new UnsafeOperationException($"Primary storage {config.PrimaryCapture} does not identify library {config.LibraryId}.");
        _listing = null;
    }

    public AssetReport ThroughMount(LibrarySnapshot snapshot) =>
        AssetInventory.Check(AssetInventory.Collect(snapshot), [config.Capture], new FileSystemProbe());

    public AssetReport OnServer(LibrarySnapshot snapshot)
    {
        RequireServer();
        _listing = List();
        return AssetInventory.Check(AssetInventory.Collect(snapshot), [config.Capture], new ListingProbe(config.Capture, _listing));
    }

    public bool ExistsOnServer(string capturePath) =>
        WindowsPath.TryParse(capturePath, out var path, out _) && path.IsWithin(config.Capture)
        && new ListingProbe(config.Capture, _listing ??= List()).Probe(path, config.Capture).Exists;

    public long? SizeOnServer(string capturePath) =>
        WindowsPath.TryParse(capturePath, out var path, out _) && path.IsWithin(config.Capture)
        && (_listing ??= List()).TryGetValue(string.Join('/', path.Segments.Skip(config.Capture.Segments.Count)), out long bytes)
            ? bytes : null;

    /// <summary>Creates the identity marker on an empty primary folder, or verifies an existing one.</summary>
    public void Initialize(bool allowExistingMedia)
    {
        try
        {
            RequireServer();
            return;
        }
        catch (UnsafeOperationException) when (!MarkerPresent()) { }
        _ = List(allowMissing: true);
        if (!allowExistingMedia && _hasEntries) throw new UnsafeOperationException($"Primary storage {config.PrimaryCapture} already contains files but no marker; choose an empty dedicated folder.");
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

    private Dictionary<string, long> List(bool allowMissing = false)
    {
        string json;
        _hasEntries = false;
        try { json = Run(Common("lsjson", config.PrimaryCapture, "--recursive", "--no-mimetype")); }
        catch (RcloneFailedException ex) when (allowMissing && ex.ExitCode == 3) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (IOException ex) when (ex is not (StorageOfflineException or StorageActionRequiredException))
        {
            throw Unavailable(ex, "cannot be listed right now");
        }
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                _hasEntries = true;
                if (!item.TryGetProperty("Path", out var path) || path.ValueKind != JsonValueKind.String
                    || path.GetString() is not { } relative || relative.Contains('\\') || relative.StartsWith('/')
                    || relative.Split('/').Any(segment => string.IsNullOrEmpty(segment) || segment is "." or "..")
                    || !WindowsPath.TryParse(config.Capture.Display + "\\" + relative.Replace('/', '\\'), out var parsed, out _)
                    || !parsed.IsWithin(config.Capture) || parsed.Equals(config.Capture)
                    || item.TryGetProperty("IsLink", out var link) && link.ValueKind == JsonValueKind.True)
                    throw new UnsafeOperationException("Primary storage returned an unsafe file identity; verification was refused.");
                if (!identities.Add(relative)) throw new UnsafeOperationException("Primary storage returned an ambiguous file identity; verification was refused.");
                if (item.TryGetProperty("IsDir", out var dir) && dir.ValueKind == JsonValueKind.True) continue;
                if (!item.TryGetProperty("Size", out var size) || !size.TryGetInt64(out long bytes) || bytes < 0
                    || !files.TryAdd(relative, bytes))
                    throw new UnsafeOperationException("Primary storage returned an ambiguous file identity; verification was refused.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new UnsafeOperationException("rclone returned an unreadable listing for primary storage; nothing was assumed missing.");
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

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Storage;
using MedalSync.Util;
using Microsoft.Win32;

namespace MedalSync.Platform;

internal sealed record DriveSummary(string Letter, string Type, string? Format, double? TotalGb, double? FreeGb);

internal sealed record TreeSummary(string Root, int Files, long Bytes, int UnreferencedFiles, long UnreferencedBytes, IReadOnlyDictionary<string, int> TopLevelFiles);

internal sealed record LibrarySummary(
    string DatabasePath,
    string AccountId,
    long SchemaVersion,
    string SchemaFingerprint,
    bool SchemaSupported,
    IReadOnlyDictionary<string, int> Rows,
    string? CaptureFolder,
    double? BitrateMbps,
    IReadOnlyDictionary<string, int> AssetProblems,
    int BlockingAssets,
    int UnrecognizedEmbeddedPaths);

internal sealed record InventoryReport(
    DateTimeOffset At,
    string Computer,
    string User,
    string Os,
    string? MedalApp,
    string? MedalRecorder,
    bool MedalRunning,
    LibrarySummary? Library,
    string? LibraryError,
    TreeSummary? CaptureTree,
    IReadOnlyList<DriveSummary> Drives,
    string ProposedDrive,
    bool ProposedDriveFree,
    CapacityVerdict? Capacity,
    IReadOnlyDictionary<string, string?> StartupEntries,
    IReadOnlyList<SyncFolder> SyncFolders,
    IReadOnlyList<ExclusionStatus> SyncExclusions,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyDictionary<string, string> Server,
    string? StateRoot = null,
    MedalCompatibilityReport? Compatibility = null,
    IReadOnlyList<StorageVolumeSummary>? StorageVolumes = null);

/// <summary>
/// The read-only Phase 0 inventory (plan Gate A). Reads the live library inside
/// one read transaction; changes nothing anywhere.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Inventory
{
    public static InventoryReport Collect(MedalSyncConfig? config, LocalState state, DateTimeOffset now)
    {
        var install = Try(() => MedalEnvironment.Locate(), out string? locateError);
        LibrarySummary? library = null;
        TreeSummary? tree = null;
        string? libraryError = locateError;
        if (install is not null)
        {
            try
            {
                (library, tree) = Library(install);
            }
            catch (Exception ex) when (ex is MedalSyncException or IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                libraryError = ex.Message;
            }
        }
        string proposed = config?.DriveRoot.Display ?? @"M:\";
        string cacheRoot = config?.CacheRoot ?? @"E:\MedalCache";
        CapacityVerdict? capacity = null;
        // The cache's own volume, which may be a disk mounted inside a folder rather than its drive letter.
        VolumeInfo? cacheVolume;
        try { cacheVolume = StorageVolume.Describe(cacheRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cacheVolume = null; }
        if (library?.BitrateMbps is double bitrate && cacheVolume is not null)
        {
            capacity = CapacityAdmission.Evaluate(new CapacityInputs(cacheVolume.FreeBytes,
                (long)(config?.Mount.MinFreeGiB ?? 20) << 30, bitrate, TimeSpan.FromHours(config?.MaxSessionHours ?? 4), 0));
        }
        string legacyProfiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LarsonServer");
        var folders = Try(() => SyncthingCheck.Folders(legacyProfiles), out _) ?? [];
        var exclusions = config?.SyncExclusions ?? [];
        return new InventoryReport(now, Environment.MachineName, Environment.UserName, RuntimeInformation.OSDescription,
            install?.Versions.App, install?.Versions.Recorder, install is not null && new MedalProcess(install).IsRunning(),
            library, libraryError, tree, Drives(), proposed, !Directory.Exists(proposed), capacity, StartupEntries(),
            folders, SyncthingCheck.Check(folders, exclusions), Dependencies(config), Server(config), state.Root,
            install is null ? null : MedalCompatibility.Check(install), StorageVolume.Summaries(config));
    }

    private static (LibrarySummary, TreeSummary?) Library(MedalInstall install)
    {
        using var connection = LibraryDatabase.OpenReadOnly(install.DatabasePath);
        using var transaction = connection.BeginTransaction(deferred: true);
        var schema = MedalSchema.Read(connection, transaction);
        string? capture = Text(LibraryDatabase.ReadSetting(connection, transaction, "ClipFolder"));
        double? bitrate = double.TryParse(LibraryDatabase.ReadSetting(connection, transaction, "Bitrate"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double b) ? b : null;
        if (!schema.IsSupported)
        {
            return (new LibrarySummary(install.DatabasePath, install.AccountId, schema.UserVersion, schema.Fingerprint, false,
                new Dictionary<string, int>(), capture, bitrate, new Dictionary<string, int>(), 0, 0), null);
        }
        var snapshot = LibraryDatabase.ReadSnapshot(connection, transaction, install.AccountId, schema);
        var references = AssetInventory.Collect(snapshot);
        WindowsPath? root = capture is not null && WindowsPath.TryParse(capture, out var parsed, out _) ? parsed : null;
        var report = AssetInventory.Check(references, root is null ? [] : [root], new FileSystemProbe());
        var summary = new LibrarySummary(install.DatabasePath, install.AccountId, schema.UserVersion, schema.Fingerprint, true,
            MedalSchema.LibraryTables.ToDictionary(t => t, snapshot.RowCount),
            capture, bitrate,
            report.Items.GroupBy(i => $"{i.Reference.Role}/{i.Problem}").ToDictionary(g => g.Key, g => g.Count()),
            report.Blocking.Count(), references.Count(r => r.Role == AssetRole.Unrecognized));
        return (summary, root is null ? null : Tree(root, references));
    }

    private static TreeSummary? Tree(WindowsPath root, IReadOnlyList<AssetReference> references)
    {
        if (!Directory.Exists(root.Display)) return null;
        var referenced = references.Select(r => r.Path.Replace('/', '\\').ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        int files = 0, unreferenced = 0;
        long bytes = 0, unreferencedBytes = 0;
        var top = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(root.Display).EnumerateFiles("*", options))
        {
            files++;
            bytes += file.Length;
            string relative = Path.GetRelativePath(root.Display, file.FullName);
            string first = relative.Contains(Path.DirectorySeparatorChar) ? relative[..relative.IndexOf(Path.DirectorySeparatorChar)] : "(root)";
            top[first] = top.GetValueOrDefault(first) + 1;
            if (!referenced.Contains(file.FullName.ToUpperInvariant()))
            {
                unreferenced++;
                unreferencedBytes += file.Length;
            }
        }
        return new TreeSummary(root.Display, files, bytes, unreferenced, unreferencedBytes, top);
    }

    private static IReadOnlyList<DriveSummary> Drives() =>
        DriveInfo.GetDrives().Select(d =>
        {
            bool ready = d.IsReady;
            return new DriveSummary(d.Name, d.DriveType.ToString(), ready ? d.DriveFormat : null,
                ready ? Math.Round(d.TotalSize / 1e9, 1) : null, ready ? Math.Round(d.AvailableFreeSpace / 1e9, 1) : null);
        }).ToList();

    /// <summary>Every place that can start Medal, marked when it goes through the launcher.</summary>
    private static IReadOnlyDictionary<string, string?> StartupEntries() =>
        new SortedDictionary<string, string?>(
            Platform.StartupEntries.Read().ToDictionary(
                e => e.Name,
                string? (e) => e.Command is null
                    ? (e.ViaLauncher ? "via medal-sync" : "starts Medal directly")
                    : e.Command + (e.ViaLauncher ? " (via medal-sync)" : "")),
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> Dependencies(MedalSyncConfig? config)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? winfsp = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")?.GetValue("InstallDir") as string;
        result["WinFsp"] = winfsp is not null && File.Exists(Path.Combine(winfsp, "bin", "winfsp-x64.dll")) ? $"installed at {winfsp}" : "not installed";
        string rclone = config?.ExpandedRclonePath
            ?? Cli.ConfigCommands.DefaultRclonePath();
        result["rclone"] = File.Exists(rclone) ? $"present at {rclone} (sha256 {Hashing.Sha256File(rclone)})" : $"not present at {rclone}";
        return result;
    }

    private static IReadOnlyDictionary<string, string> Server(MedalSyncConfig? config)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (config is null)
        {
            result["remote"] = "not configured";
            result["coordination"] = "not configured";
            return result;
        }
        foreach (var (name, path) in new[]
        {
            ("remote", config.Remote),
            ("coordination", config.CoordinationRoot),
        })
        {
            if (name == "remote" && config.PrimaryIsRclone)
            {
                // A provider folder is not a path on this PC; launch and finish contact it with the configured credentials.
                result[name] = $"rclone remote {config.Remote}; checked at launch and finish";
                continue;
            }
            string? share = WindowsPath.TryParse(path, out var parsed, out _) ? parsed.Root : null;
            result[name] = Directory.Exists(path)
                ? FreeSpace(path) is (long free, long total) ? $"reachable; {free / 1e9:0.0} GB free of {total / 1e9:0.0} GB" : "reachable"
                : share is not null && Directory.Exists(share)
                    ? $"share reachable; {path} is not created yet (init creates it)"
                    : $"not reachable: {path}";
        }
        return result;
    }

    internal static (long Free, long Total)? FreeSpace(string path) =>
        GetDiskFreeSpaceEx(path.TrimEnd('\\') + "\\", out ulong available, out ulong total, out _) ? ((long)available, (long)total) : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeAvailable, out ulong total, out ulong totalFree);

    private static string? Text(string? json)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
    }

    private static T? Try<T>(Func<T> action, out string? error) where T : class
    {
        try
        {
            error = null;
            return action();
        }
        catch (Exception ex) when (ex is MedalSyncException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            error = ex.Message;
            return null;
        }
    }
}

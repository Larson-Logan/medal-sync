using MedalSync.Coordination;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Config;

internal sealed record MountConfig(
    int WriteBackSeconds,
    int CacheMaxGiB,
    int MinFreeGiB,
    int CacheMaxAgeSeconds,
    int CachePollSeconds,
    int DirCacheHours,
    int Transfers,
    int ControlPort);

/// <summary>A Syncthing folder that overlaps the clip destination and must ignore it.</summary>
internal sealed record SyncExclusion(string FolderId, string Pattern);

internal enum StorageMode { Server, Local }

/// <summary>
/// Per-PC settings in the resolved Medal Sync user state directory.
/// Holds no credentials: SMB access uses the PC's existing saved login.
/// </summary>
internal sealed record MedalSyncConfig(
    int Format,
    string LibraryId,
    string AccountId,
    string CoordinationRoot,
    string Remote,
    string CaptureFolder,
    string CacheRoot,
    string RclonePath,
    MountConfig Mount,
    double MaxSessionHours,
    IReadOnlyList<SyncExclusion> SyncExclusions)
{
    public const int CurrentFormat = 1;

    /// <summary>Local mode owns its journal and persistent media on this PC; server mode shares ownership through SMB.</summary>
    public StorageMode StorageMode { get; init; } = StorageMode.Server;

    /// <summary>Optional persistent local storage and required media replicas.</summary>
    public ReplicationConfig? Replication { get; init; }

    public string MountRemote => Replication?.LocalCopyRoot ?? Remote;

    public WindowsPath MountedCapture => Capture.Rebase(DriveRoot, WindowsPath.Parse(MountRemote));

    /// <summary>
    /// Written in the capture folder so a wrong or empty share is recognized. It
    /// sits inside the folder Syncthing must ignore, so it is never synchronized.
    /// </summary>
    public const string StorageMarker = ".medal-sync-library";

    public WindowsPath Capture => WindowsPath.Parse(CaptureFolder);

    public WindowsPath DriveRoot => WindowsPath.Parse(Capture.Root);

    public WindowsPath RemoteRoot => WindowsPath.Parse(Remote);

    /// <summary>The capture folder as seen on the server, outside the mount.</summary>
    public WindowsPath ServerCapture => Capture.Rebase(DriveRoot, RemoteRoot);

    public string LibraryCoordinationDirectory => WindowsPath.Parse(CoordinationRoot).Append(LibraryId).Display;

    public string SessionCacheDirectory(string session) => WindowsPath.Parse(CacheRoot).Append(LibraryId, session).Display;

    public string ExpandedRclonePath => Environment.ExpandEnvironmentVariables(RclonePath);

    public MountSettings MountSettings(string session, string logFile) => new(
        MountRemote, Capture.Root[0], SessionCacheDirectory(session),
        TimeSpan.FromSeconds(Mount.WriteBackSeconds), (long)Mount.CacheMaxGiB << 30, (long)Mount.MinFreeGiB << 30,
        TimeSpan.FromSeconds(Mount.CacheMaxAgeSeconds), TimeSpan.FromSeconds(Mount.CachePollSeconds),
        TimeSpan.FromHours(Mount.DirCacheHours), Mount.Transfers, Mount.ControlPort, "Medal Clips", logFile);

    public static MedalSyncConfig Load(string path)
    {
        if (!File.Exists(path)) throw new UnsafeOperationException($"medal-sync is not set up on this PC: '{path}' does not exist.");
        var config = Json.ReadFile<MedalSyncConfig>(path, "medal-sync configuration");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        var problems = new List<string>();
        if (Format != CurrentFormat) problems.Add($"format {Format} is not supported");
        if (!CoordinationStore.IsValidId(LibraryId)) problems.Add("libraryId must be 32 lowercase hex characters");
        if (string.IsNullOrEmpty(AccountId) || !AccountId.All(char.IsAsciiDigit)) problems.Add("accountId must be the numeric Medal account ID");
        if (!Enum.IsDefined(StorageMode)) problems.Add("storageMode is not supported");
        if (StorageMode == StorageMode.Server)
        {
            if (!IsUnc(CoordinationRoot)) problems.Add("coordinationRoot must be a UNC path on the private share");
            if (!IsUnc(Remote)) problems.Add("remote must be a UNC path on the private share");
        }
        else
        {
            if (!IsLocalFolder(CoordinationRoot)) problems.Add("local mode requires a dedicated local coordinationRoot");
            if (!IsLocalFolder(Remote)) problems.Add("local mode requires a persistent local storage folder as remote");
            if (Replication?.LocalCopyRoot is not null) problems.Add("local mode already retains the remote folder; omit localCopyRoot");
            if (WindowsPath.TryParse(Remote, out var local, out _))
            {
                foreach (string other in new[] { CoordinationRoot, CacheRoot, CaptureFolder })
                    if (WindowsPath.TryParse(other, out var path, out _) && (path.IsWithin(local) || local.IsWithin(path)))
                        problems.Add("local storage must not overlap coordination, cache or capture paths");
            }
            if (WindowsPath.TryParse(CoordinationRoot, out var coordination, out _))
            {
                foreach (string other in new[] { CacheRoot, CaptureFolder })
                    if (WindowsPath.TryParse(other, out var path, out _) && (path.IsWithin(coordination) || coordination.IsWithin(path)))
                        problems.Add("local coordination must not overlap cache or capture paths");
            }
        }
        if (!WindowsPath.TryParse(CaptureFolder, out var capture, out _) || capture.Root.StartsWith(@"\\", StringComparison.Ordinal) || capture.Segments.Count == 0)
        {
            problems.Add("captureFolder must be a folder on the virtual drive, such as M:\\Clips (Medal rejects drive roots)");
        }
        if (!WindowsPath.TryParse(CacheRoot, out var cache, out _) || cache.Root.StartsWith(@"\\", StringComparison.Ordinal)
            || (capture is not null && cache.Root == capture.Root))
        {
            problems.Add("cacheRoot must be a local SSD folder, not the share or the virtual drive");
        }
        if (string.IsNullOrWhiteSpace(RclonePath)) problems.Add("rclonePath is required");
        if (Mount is null) problems.Add("mount settings are required");
        else if (Mount.DirCacheHours < 1 || Mount.DirCacheHours > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerHour)
            problems.Add("mount.dirCacheHours must be a positive duration within the supported range");
        if (SyncExclusions is null) problems.Add("syncExclusions must be an array");
        else
            foreach (var exclusion in SyncExclusions)
            {
                if (exclusion is null) { problems.Add("syncExclusions cannot contain null"); continue; }
                if (string.IsNullOrWhiteSpace(exclusion.FolderId)) problems.Add("each sync exclusion requires a folderId");
                string? pattern = exclusion.Pattern;
                if (pattern is null || pattern.Length < 2 || pattern[0] != '/' || pattern.EndsWith('/') || pattern.Contains('\\')
                    || !WindowsPath.TryParse(@"X:\" + pattern[1..], out _, out _))
                    problems.Add("each sync exclusion requires one rooted folder pattern such as /Videos/Clips; no wildcards or relative segments");
            }
        if (Replication is not null) problems.AddRange(Replication.Problems(this));
        if (!double.IsFinite(MaxSessionHours) || MaxSessionHours is <= 0 or > 24) problems.Add("maxSessionHours must be between 0 and 24");
        if (problems.Count == 0)
        {
            try
            {
                MountPlan.Validate(MountSettings(new string('0', 32), WindowsPath.Parse(CacheRoot).Append("rclone.log").Display));
            }
            catch (ArgumentException ex)
            {
                problems.Add(ex.Message);
            }
        }
        if (problems.Count > 0) throw new InvalidDataException("The medal-sync configuration is invalid: " + string.Join("; ", problems) + ".");
    }

    private static bool IsUnc(string value) =>
        WindowsPath.TryParse(value, out var path, out _) && path.Root.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool IsLocalFolder(string value) =>
        WindowsPath.TryParse(value, out var path, out _) && !path.Root.StartsWith(@"\\", StringComparison.Ordinal) && path.Segments.Count > 0;
}

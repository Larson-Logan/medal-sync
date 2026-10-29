using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>
/// Checks library files through the mounted drive (what Medal will see) and
/// directly on the share (what is really stored), translating M:\Clips paths
/// to the share's own path. Reports always name the M:\ path Medal uses.
/// </summary>
internal static class PrimaryStorage
{
    /// <summary>The checker for this configuration's primary: a share or local folder, or a named rclone remote.</summary>
    public static IAssetChecker Checker(MedalSyncConfig config, LocalState state, Action? requireTime = null) => config.PrimaryIsRclone
        ? new RcloneAssetChecker(config, state, requireTime: requireTime)
        : new ShareAssetChecker(config.Capture, config.ServerCapture, config.LibraryId, new FileSystemProbe());
}

internal sealed class ShareAssetChecker(WindowsPath capture, WindowsPath serverCapture, string libraryId, IFileProbe probe) : IAssetChecker
{
    public void RequireServer()
    {
        var found = probe.Probe(serverCapture.Append(MedalSyncConfig.StorageMarker), WindowsPath.Parse(serverCapture.Root));
        if (found.HasReparsePoint || found.IsDirectory)
            throw new UnsafeOperationException("Primary storage contains a reparse point or invalid marker; verification was refused.");
        string marker = serverCapture.Append(MedalSyncConfig.StorageMarker).Display;
        string text;
        try
        {
            text = File.ReadAllText(marker);
        }
        catch (FileNotFoundException ex) when (Directory.Exists(serverCapture.Display))
        {
            // Reachable storage without its marker is a setup problem, not an outage.
            throw new IOException($"The clip folder on the server ({serverCapture.Display}) has no Medal Sync marker: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageOfflineException($"The clip folder on the server ({serverCapture.Display}) cannot be reached: {ex.Message}", ex);
        }
        if (text.Trim() != libraryId) throw new UnsafeOperationException($"{marker} does not identify library {libraryId}.");
    }

    public AssetReport ThroughMount(LibrarySnapshot snapshot) =>
        AssetInventory.Check(AssetInventory.Collect(snapshot), [capture], probe);

    public AssetReport OnServer(LibrarySnapshot snapshot) =>
        AssetInventory.Check(AssetInventory.Collect(snapshot), [capture], new RebasingProbe(capture, serverCapture, probe));

    public bool ExistsOnServer(string capturePath) =>
        WindowsPath.TryParse(capturePath, out var path, out _) && path.IsWithin(capture)
        && new RebasingProbe(capture, serverCapture, probe).Probe(path, capture) is { Exists: true, IsDirectory: false, HasReparsePoint: false };

    public long? SizeOnServer(string capturePath) =>
        WindowsPath.TryParse(capturePath, out var path, out _) && path.IsWithin(capture)
        && new RebasingProbe(capture, serverCapture, probe).Probe(path, capture) is { Exists: true, IsDirectory: false, HasReparsePoint: false } found
            ? found.Length : null;

    private sealed class RebasingProbe(WindowsPath from, WindowsPath to, IFileProbe inner) : IFileProbe
    {
        public FileProbeResult Probe(WindowsPath path, WindowsPath root) => inner.Probe(path.Rebase(from, to), root.Rebase(from, to));
    }
}

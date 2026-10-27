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
internal sealed class ShareAssetChecker(WindowsPath capture, WindowsPath serverCapture, string libraryId, IFileProbe probe) : IAssetChecker
{
    public void RequireServer()
    {
        string marker = serverCapture.Append(MedalSyncConfig.StorageMarker).Display;
        string text;
        try
        {
            text = File.ReadAllText(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The clip folder on the server ({serverCapture.Display}) cannot be reached: {ex.Message}", ex);
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

    private sealed class RebasingProbe(WindowsPath from, WindowsPath to, IFileProbe inner) : IFileProbe
    {
        public FileProbeResult Probe(WindowsPath path, WindowsPath root) => inner.Probe(path.Rebase(from, to), root.Rebase(from, to));
    }
}

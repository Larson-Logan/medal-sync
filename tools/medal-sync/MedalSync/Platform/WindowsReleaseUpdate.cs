using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MedalSync.Platform;

/// <summary>Explicit, configuration-independent update download. Installation stays in the guarded setup EXE.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsReleaseUpdate
{
    internal static int Run(WindowsLauncherUi ui)
    {
        const string releases = "https://github.com/Larson-Logan/medal-sync/releases";
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var asset = ReleaseUpdate.LatestAsync(client, timeout.Token, requireSetup: true).GetAwaiter().GetResult();
        if (asset is null)
        {
            ui.Show($"Could not check for Medal Sync updates. You have version {ToolInfo.Version}.\n\nCheck your connection and try again. A private repository, rate limit or missing published Windows setup can also prevent this check.\n\nReleases:\n{releases}");
            return 1;
        }
        if (Version.Parse(asset.Version) <= Version.Parse(ToolInfo.Version))
        {
            ui.Show($"Medal Sync {ToolInfo.Version} is up to date. No newer published Windows setup is available.");
            return 0;
        }
        if (!ui.Confirm($"Medal Sync {asset.Version} is available. You have {ToolInfo.Version}.\n\nDownload the Windows setup ({(asset.Size + 1048575) / 1048576} MB)? The download will be checked against the release SHA-256.\n\nYou can cancel the download. Before running setup, quit Medal normally and wait for Ready. Your settings and recordings will be kept.")) return 0;
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MedalSync", "Downloads", Guid.NewGuid().ToString("N"));
            using var downloadHandler = new HttpClientHandler { UseCookies = false };
            using var downloadClient = new HttpClient(downloadHandler) { Timeout = Timeout.InfiniteTimeSpan };
            string setup = Download(asset, directory, downloadClient);
            ui.Show($"Medal Sync {asset.Version} setup was downloaded and verified.\n\nQuit Medal normally and wait for Ready, then run:\n{setup}\n\nThe download folder will open when you close this message.");
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            return 0;
        }
        catch (OperationCanceledException)
        {
            ui.Show("The update download was canceled or timed out. Check for updates again to retry.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or COMException or Win32Exception)
        {
            ui.Show($"The update could not be completed: {ex.Message}\n\nCheck for updates again to retry, or download Windows setup from:\n{releases}");
            return 1;
        }
    }

    internal static string Download(ReleaseAsset asset, string directory, HttpClient client)
    {
        var dialog = (IProgressDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F8383852-FCD3-11D1-A6B9-006097DF5BD4"), true)!)!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            dialog.SetTitle("Medal Sync update");
            dialog.SetCancelMsg("Canceling the download. Incomplete files will be removed.", IntPtr.Zero);
            dialog.StartProgressDialog(IntPtr.Zero, null, 0x2, IntPtr.Zero); // PROGDLG_AUTOTIME
            dialog.SetLine(1, "Downloading " + asset.Name, false, IntPtr.Zero);
            dialog.SetLine(2, "The setup must pass SHA-256 verification before you can run it.", false, IntPtr.Zero);
            // Poll cancellation independently of network reads, including stalled connections.
            var poll = new Timer(_ =>
            {
                try { if (dialog.HasUserCancelled()) cancellation.Cancel(); }
                catch (COMException) { cancellation.Cancel(); }
            }, null, 200, 200);
            try
            {
                return ReleaseUpdate.DownloadSetupAsync(client, asset, directory,
                    (received, total) => dialog.SetProgress64((ulong)received, (ulong)total), cancellation.Token).GetAwaiter().GetResult();
            }
            finally { poll.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally
        {
            dialog.StopProgressDialog();
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    // Shell's native progress dialog owns its window and message pump. No UI framework or browser is bundled.
    [ComImport, Guid("EBBC7C04-315E-11D2-B62F-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProgressDialog
    {
        void StartProgressDialog(IntPtr parent, [MarshalAs(UnmanagedType.IUnknown)] object? enableModeless, uint flags, IntPtr reserved);
        void StopProgressDialog();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetAnimation(IntPtr instance, uint resource);
        [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool HasUserCancelled();
        void SetProgress(uint completed, uint total);
        void SetProgress64(ulong completed, ulong total);
        void SetLine(uint line, [MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool compactPath, IntPtr reserved);
        void SetCancelMsg([MarshalAs(UnmanagedType.LPWStr)] string message, IntPtr reserved);
    }
}

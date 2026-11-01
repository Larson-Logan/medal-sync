using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MedalSync.Platform;

/// <summary>What still has to happen before an update can be installed.</summary>
internal enum UpdateReadiness { Ready, MedalRunning, Finishing }

/// <summary>Explicit, verified update download and handoff to the guarded setup EXE.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsReleaseUpdate
{
    private const string Releases = "https://github.com/Larson-Logan/medal-sync/releases";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{168562DF-1041-441D-96D0-2244EA744E18}_is1";
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    // After Medal and supervision stop, the final handoff write can briefly lag behind process exit.
    internal static readonly TimeSpan BlockedGrace = TimeSpan.FromSeconds(15);

    /// <param name="readiness">Returns what the update waits for; throws when the library needs attention first.</param>
    /// <param name="closeStatus">Closes the status app so setup can replace program files.</param>
    internal static int Run(WindowsLauncherUi ui, Func<UpdateReadiness> readiness, Action closeStatus)
    {
        string downloads = ReleaseUpdate.DownloadsRoot();
        ReleaseUpdate.CleanupDownloads(downloads, ToolInfo.Version);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var asset = ReleaseUpdate.LatestAsync(client, timeout.Token, requireSetup: true).GetAwaiter().GetResult();
        if (asset is null)
        {
            ui.Show($"Could not check for Medal Sync updates. You have version {ToolInfo.Version}.\n\nCheck your connection and try again. A private repository, rate limit or missing published Windows setup can also prevent this check.\n\nReleases:\n{Releases}");
            return 1;
        }
        if (Version.Parse(asset.Version) <= Version.Parse(ToolInfo.Version))
        {
            ui.Show($"Medal Sync {ToolInfo.Version} is up to date. No newer published Windows setup is available.");
            return 0;
        }
        string? saved = ReleaseUpdate.FindVerifiedDownload(downloads, asset);
        string size = saved is null ? $" ({(asset.Size + 1048575) / 1048576} MB)" : "";
        if (!ui.Confirm($"Medal Sync {asset.Version} is available. You have {ToolInfo.Version}.\n\nUpdate now? Medal Sync will download the verified Windows setup{size} and install it for you.\n\nIf Medal is open, the update waits until you quit Medal and uploads finish, then installs automatically. Medal Sync reopens when it is done.\n\nYour settings and recordings will be kept.")) return 0;
        try
        {
            string setup = saved ?? DownloadNew(asset, downloads);
            string? program = ProgramDirectory(AppContext.BaseDirectory);
            string? arguments = program is null ? null : SetupArguments(InstallMode(program, RegisteredLocation), program);
            return InstallWhenReady(asset.Version, setup, readiness, check => WaitWithDialog(check, asset.Version), closeStatus,
                path =>
                {
                    // Do not wait: this updater must exit and release its program files before setup checks readiness.
                    using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = arguments ?? "" });
                    if (process is null) throw new IOException("Windows setup did not start.");
                }, ui.Show, OpenFolder);
        }
        catch (OperationCanceledException)
        {
            ui.Show("The update download was canceled or timed out. Check for updates again to retry.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or COMException or Win32Exception)
        {
            ui.Show($"The update could not be completed: {ex.Message}\n\nCheck for updates again to retry, or download Windows setup from:\n{Releases}");
            return 1;
        }
    }

    private static string DownloadNew(ReleaseAsset asset, string downloads)
    {
        using var downloadHandler = new HttpClientHandler { UseCookies = false };
        using var downloadClient = new HttpClient(downloadHandler) { Timeout = Timeout.InfiniteTimeSpan };
        return Download(asset, Path.Combine(downloads, Guid.NewGuid().ToString("N")), downloadClient);
    }

    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    /// <summary>Waits for Ready, closes the status app and starts setup. Never ends Medal or supervision.</summary>
    internal static int InstallWhenReady(string version, string setup, Func<UpdateReadiness> readiness,
        Func<Func<UpdateReadiness>, bool> wait, Action closeStatus, Action<string> startSetup, Action<string> show, Action<string> openFolder)
    {
        bool ready;
        try { ready = wait(readiness); }
        catch (Exception ex) when (IsBlocking(ex))
        {
            show($"Medal Sync {version} was not installed: {ex.Message}\n\nThe verified installer is saved here:\n{setup}\n\nResolve this, then choose Check for updates again. It installs the saved update without downloading it again.");
            openFolder(Path.GetDirectoryName(setup)!);
            return 1;
        }
        if (!ready)
        {
            show($"The update to Medal Sync {version} is postponed. The verified installer is saved, so Check for updates installs it later without downloading again.");
            return 0;
        }
        try
        {
            closeStatus();
            startSetup(setup);
            return 0;
        }
        catch (Exception ex) when (IsBlocking(ex) || ex is Win32Exception)
        {
            show($"Windows setup could not start: {ex.Message}\n\nThe verified installer is saved here:\n{setup}\n\nThe download folder will open. Resolve the issue, then run this installer or Check for updates to retry without downloading again.");
            openFolder(Path.GetDirectoryName(setup)!);
            return 1;
        }
    }

    private static bool IsBlocking(Exception ex) => ex is IOException or UnauthorizedAccessException or MedalSyncException;

    /// <summary>
    /// Polls until the library is Ready. Medal running or an active supervisor is an ordinary wait. A refusal once
    /// both have stopped is retried for <paramref name="grace"/> before it is reported, so the handoff can complete.
    /// </summary>
    internal static bool WaitUntilReady(Func<UpdateReadiness> readiness, Action<UpdateReadiness> report, Func<bool> canceled,
        Action<TimeSpan> sleep, Func<DateTimeOffset> now, TimeSpan? grace = null)
    {
        DateTimeOffset? blockedSince = null;
        UpdateReadiness? reported = null;
        while (!canceled())
        {
            UpdateReadiness state;
            try
            {
                state = readiness();
                blockedSince = null;
            }
            catch (Exception ex) when (IsBlocking(ex))
            {
                blockedSince ??= now();
                if (now() - blockedSince >= (grace ?? BlockedGrace)) throw;
                state = UpdateReadiness.Finishing;
            }
            if (state == UpdateReadiness.Ready) return true;
            if (state != reported) { report(state); reported = state; }
            sleep(PollInterval);
        }
        return false;
    }

    private static bool WaitWithDialog(Func<UpdateReadiness> readiness, string version)
    {
        IProgressDialog? dialog = null;
        try
        {
            return WaitUntilReady(readiness, state =>
            {
                if (dialog is null)
                {
                    dialog = CreateProgressDialog();
                    dialog.SetTitle("Medal Sync update");
                    dialog.SetCancelMsg("Postponing the update. The verified installer is kept.", IntPtr.Zero);
                    dialog.StartProgressDialog(IntPtr.Zero, null, 0x4 | 0x20, IntPtr.Zero); // PROGDLG_NOTIME | PROGDLG_MARQUEEPROGRESS
                    dialog.SetLine(3, "Choose Cancel to install later.", false, IntPtr.Zero);
                }
                var (title, detail) = WaitText(state, version);
                dialog.SetLine(1, title, false, IntPtr.Zero);
                dialog.SetLine(2, detail, false, IntPtr.Zero);
            }, () =>
            {
                try { return dialog?.HasUserCancelled() == true; }
                catch (COMException) { return true; }
            }, Thread.Sleep, () => DateTimeOffset.UtcNow);
        }
        finally
        {
            if (dialog is not null)
            {
                dialog.StopProgressDialog();
                Marshal.FinalReleaseComObject(dialog);
            }
        }
    }

    internal static (string Title, string Detail) WaitText(UpdateReadiness state, string version) => state switch
    {
        UpdateReadiness.MedalRunning => ("Waiting for you to quit Medal",
            $"Quit Medal from its tray icon when you are done. Medal Sync then finishes uploads and installs {version} for you."),
        _ => ("Finishing uploads before updating",
            $"Medal Sync is returning the library to Ready. Version {version} installs automatically when this finishes."),
    };

    /// <summary>The program folder when this executable runs from an installed bin folder; otherwise null.</summary>
    internal static string? ProgramDirectory(string baseDirectory)
    {
        string bin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        return string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(bin) : null;
    }

    /// <summary>
    /// The setup install mode for this program folder: its Installed apps registration first, then its standard
    /// location. Null when unknown, so the interactive wizard lets the user choose.
    /// </summary>
    internal static string? InstallMode(string program, Func<RegistryHive, string?> registered,
        string? programFiles = null, string? userPrograms = null)
    {
        if (SameDirectory(registered(RegistryHive.CurrentUser), program)) return "/CURRENTUSER";
        if (SameDirectory(registered(RegistryHive.LocalMachine), program)) return "/ALLUSERS";
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        userPrograms ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (Within(program, userPrograms)) return "/CURRENTUSER";
        if (Within(program, programFiles)) return "/ALLUSERS";
        return null;
    }

    /// <summary>
    /// Arguments for an unattended upgrade of the same folder that reopens the status app afterwards. Setup still runs
    /// its own readiness check and reports any refusal. Null keeps the interactive wizard.
    /// </summary>
    internal static string? SetupArguments(string? mode, string program) =>
        mode is null || program.Contains('"', StringComparison.Ordinal) ? null
            : $"/SILENT /NORESTART {mode} /DIR=\"{Path.TrimEndingDirectorySeparator(program)}\" /MEDALSYNCRELAUNCH=status";

    private static string? RegisteredLocation(RegistryHive hive)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static bool SameDirectory(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool Within(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent)) return false;
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Download(ReleaseAsset asset, string directory, HttpClient client)
    {
        var dialog = CreateProgressDialog();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            dialog.SetTitle("Medal Sync update");
            dialog.SetCancelMsg("Canceling the download. Incomplete files will be removed.", IntPtr.Zero);
            dialog.StartProgressDialog(IntPtr.Zero, null, 0x2, IntPtr.Zero); // PROGDLG_AUTOTIME
            dialog.SetLine(1, "Downloading " + asset.Name, false, IntPtr.Zero);
            dialog.SetLine(2, "The setup must pass SHA-256 verification before it is installed.", false, IntPtr.Zero);
            // The dialog belongs to this (STA) thread and has no cross-apartment marshaling, so only this thread
            // touches it. The download runs on the thread pool and only records progress; this loop polls
            // cancellation independently of network reads, including stalled connections.
            long received = 0;
            var download = Task.Run(() => ReleaseUpdate.DownloadSetupAsync(client, asset, directory,
                (value, _) => Interlocked.Exchange(ref received, value), cancellation.Token));
            // WhenAny never faults, so the original cancellation or download exception reaches the caller unwrapped.
            var finished = Task.WhenAny(download);
            while (!finished.Wait(200))
            {
                try
                {
                    if (dialog.HasUserCancelled()) cancellation.Cancel();
                    else dialog.SetProgress64((ulong)Interlocked.Read(ref received), (ulong)asset.Size);
                }
                catch (COMException) { cancellation.Cancel(); }
            }
            return download.GetAwaiter().GetResult();
        }
        finally
        {
            dialog.StopProgressDialog();
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    private static IProgressDialog CreateProgressDialog() =>
        (IProgressDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F8383852-FCD3-11D1-A6B9-006097DF5BD4"), true)!)!;

    // Shell's native progress dialog owns its window and message pump independently of the tray view.
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

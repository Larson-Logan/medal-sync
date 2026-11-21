using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Platform;

namespace MedalSync.Cli;

/// <summary>
/// <c>storage sign-in --remote NAME [--provider drive|onedrive|dropbox] [--rclone-config FILE]</c>: creates or reconnects
/// one rclone remote through the provider's sign-in page in the default browser, then runs the storage check for it.
/// The status panel's Sign in again runs this. Exit 0 when signed in and checked, 1 otherwise.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SignInCommands
{
    public static int Run(Arguments args, TextWriter output)
    {
        string remote = args.Required("--remote");
        string? providerName = args.Value("--provider");
        string? configFile = args.Value("--rclone-config");
        string? rclonePath = args.Value("--rclone-path");
        args.RequireAllUsed();
        CloudProvider? provider = providerName is null ? null : Provider(providerName);
        var state = LocalState.ForCurrentUser();
        var config = File.Exists(state.ConfigPath) ? MedalSyncConfig.Load(state.ConfigPath) : null;
        using var instance = HoldNoSession(config);
        configFile ??= config?.Replication?.RcloneConfigPath;
        string rclone = rclonePath ?? config?.ExpandedRclonePath ?? ConfigCommands.DefaultRclonePath();
        if (!File.Exists(Environment.ExpandEnvironmentVariables(rclone))) throw new UnsafeOperationException($"rclone is not installed at '{rclone}'.");
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler stop = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += stop;
        try
        {
            output.WriteLine("Opening the provider's sign-in page in your browser. Finish signing in there; press Ctrl+C to cancel.");
            if (provider == CloudProvider.GoogleDrive || provider is null && remote.Contains("drive", StringComparison.OrdinalIgnoreCase))
                output.WriteLine(ProviderSignIn.GoogleTestingWarning + " See " + ProviderSignIn.GoogleClientGuide);
            var result = new ProviderSignIn(Environment.ExpandEnvironmentVariables(rclone), state).SignIn(remote, provider, configFile, cancel.Token);
            output.WriteLine(result.Message);
            if (result.Succeeded && config is not null && StorageCheck.HasRemotes(config)
                && Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!) == result.ConfigPath)
            {
                // Refresh the saved check, so the status panel stops asking for a sign-in that is now fixed.
                try { StorageCheck.Run(config, DateTimeOffset.UtcNow).Save(state); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The next launch checks again. */ }
            }
            return result.Succeeded ? 0 : 1;
        }
        finally { Console.CancelKeyPress -= stop; }
    }

    internal static CloudProvider Provider(string name) => name.ToLowerInvariant() switch
    {
        "drive" or "gdrive" or "google-drive" => CloudProvider.GoogleDrive,
        "onedrive" => CloudProvider.OneDrive,
        "dropbox" => CloudProvider.Dropbox,
        _ => throw new UsageException("--provider must be drive, onedrive or dropbox."),
    };

    /// <summary>
    /// rclone's sign-in page answers on its own local port, which an older clip drive may use (#102), and a running
    /// session must keep its credential file stable, so sign-in runs only when no session runs on this PC. The returned
    /// instance lock is held on the calling thread until the credential file is replaced or left alone: no session can
    /// start, and no second sign-in can copy the same file, in the meantime.
    /// </summary>
    internal static IDisposable HoldNoSession(MedalSyncConfig? config)
    {
        IDisposable instance;
        try { instance = SessionCommands.SingleInstance(); }
        catch (UnsafeOperationException) { throw SessionRunning(); }
        if (config is not null && Directory.Exists(config.DriveRoot.Display))
        {
            instance.Dispose();
            throw SessionRunning();
        }
        return instance;
    }

    private static UnsafeOperationException SessionRunning() => new("Sign in once Medal is closed and this PC's session has "
        + "finished (Ready), and no other sign-in is running. The clip drive needs a stable credential file, and rclone's "
        + "sign-in page needs its local port.");
}

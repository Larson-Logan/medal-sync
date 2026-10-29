using MedalSync.Cli;
using MedalSync.Launch;
using MedalSync.Config;
using MedalSync.Platform;
using MedalSync.Util;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        var ui = new WindowsLauncherUi();
        try
        {
            if (args is ["--status"] or ["--tray"]) return WindowsTrayStatus.Run(args[0] == "--status");
            if (args is ["--check-updates"]) return WindowsReleaseUpdate.Run(ui);
            if (args is ["--configure"]) return WindowsConfigurationWizard.Run(LocalState.ForCurrentUser());
            return Launcher.Run(args, ui, state => WindowsConfigurationWizard.Run(state));
        }
        catch (Exception ex)
        {
            // Without a console an unexpected failure would otherwise vanish.
            try
            {
                new RotatingLog(LocalState.ForCurrentUser().LogDirectory, "medal-sync-launch").Write("crash: " + ex);
            }
            catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
            {
                // The message box below still reports the failure.
            }
            ui.Show($"medal-sync stopped unexpectedly: {ex.Message}\n\nDetails are in the logs directory of your Medal Sync user data folder.");
            return 1;
        }
    }
}

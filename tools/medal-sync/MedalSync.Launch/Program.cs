using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Platform;
using MedalSync.Util;

if (!OperatingSystem.IsWindows()) return 2;
var ui = new WindowsLauncherUi();
try
{
    if (args is ["--check-updates"]) return WindowsReleaseUpdate.Run(ui);
    return Launcher.Run(args, ui);
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

using LarsonServer.MedalSync.Cli;
using LarsonServer.MedalSync.Config;
using LarsonServer.MedalSync.Platform;
using LarsonServer.MedalSync.Util;

if (!OperatingSystem.IsWindows()) return 2;
var ui = new WindowsLauncherUi();
try
{
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

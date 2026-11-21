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
        var ui = new WindowsLauncherUi(ChooseHandoff);
        try
        {
            // Point startup at this copy as soon as it runs (for example the status view setup reopens),
            // not only after the next supervised session, so an older copy is not started at sign-in.
            SessionCommands.GuardStartup(
                () => StartupEntries.EnforceExisting(LocalState.ForCurrentUser().Root, AppContext.BaseDirectory),
                message => new RotatingLog(LocalState.ForCurrentUser().LogDirectory, "medal-sync-launch").Write(message));
            if (args is ["--status"] or ["--tray"]) return WindowsTrayStatus.Run(args[0] == "--status");
            if (args is ["--check-updates"]) return WindowsReleaseUpdate.Run(ui, () =>
            {
                // Wait for the user to quit Medal and for supervision to finish its handoff; never end either.
                if (SetupMaintenance.MedalRunning()) return UpdateReadiness.MedalRunning;
                if (SessionCommands.AnotherInstanceRunning()) return UpdateReadiness.Finishing;
                SetupMaintenance.RequireReady(LocalState.ForCurrentUser().Root, false, false);
                return UpdateReadiness.Ready;
            }, () => WindowsTrayStatus.CloseForUpdate());
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

    private static HandoffAction ChooseHandoff(HandoffWaitOffer offer)
    {
        // TaskDialog needs common controls v6; an ordinary launch reaches this without the tray or the wizard.
        Application.EnableVisualStyles();
        var wait = new TaskDialogButton("Wait and open when ready");
        var useLastSync = new TaskDialogButton("Use last synced library");
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = "Medal Sync",
            Heading = "Another PC owns the library",
            Text = offer.Message + (offer.Recovery is null ? "" : "\n\nIf the other PC is powered off, you can open the library from the last completed sync."),
            DefaultButton = cancel,
        };
        page.Buttons.Add(wait);
        if (offer.Recovery is not null) page.Buttons.Add(useLastSync);
        page.Buttons.Add(cancel);
        var answer = TaskDialog.ShowDialog(page);
        if (answer == wait) return HandoffAction.Wait;
        return answer == useLastSync ? HandoffAction.UseLastSync : HandoffAction.Cancel;
    }
}

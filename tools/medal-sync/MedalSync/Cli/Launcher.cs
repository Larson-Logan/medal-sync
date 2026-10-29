using System.Globalization;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Cli;

/// <summary>How the windowless launcher talks to the user.</summary>
internal interface ILauncherUi
{
    /// <summary>Shows a message and waits until it is closed (used just before the launcher exits).</summary>
    void Show(string message);

    /// <summary>Shows a message without waiting or taking focus (used while Medal runs, perhaps under a game).</summary>
    void Notify(string message);

    /// <summary>Explicit review of every suspect removal, only with Medal stopped.</summary>
    bool ConfirmLibraryChanges(LibraryChangeReview review);

    /// <summary>Offer the native configuration flow on an explicit, unconfigured launch.</summary>
    bool OfferConfiguration(string message) { Show(message); return false; }
}

internal sealed record LauncherRequest(bool AtSignIn, string? Link);

internal enum LauncherStep
{
    /// <summary>A session is already supervised: just open Medal's window (or pass the link).</summary>
    Forward,

    /// <summary>The last session is still finishing (uploads) after Medal quit.</summary>
    Busy,

    /// <summary>Run <c>launch</c> and supervise until Medal exits.</summary>
    Launch,
}

/// <summary>
/// <c>medal-sync-launch.exe</c>: what Medal's Run value, shortcuts and
/// <c>medal://</c> handler start once startup is rewired. It runs <c>launch</c>
/// without a console window and shows the outcome only when something needs
/// the user.
/// </summary>
internal static partial class Launcher
{
    public const int SignInWaitSeconds = 120;
    public const int ManualWaitSeconds = 20;

    public const string BusyMessage =
        "Medal Sync is still uploading or verifying the last session. Wait for Ready before reopening Medal or switching PCs.";

    public static LauncherRequest Parse(IReadOnlyList<string> args) => args switch
    {
        [] => new LauncherRequest(false, null),
        [StartupEntries.SignInFlag] => new LauncherRequest(true, null),
        [var link] when MedalLink.IsLink(link) => new LauncherRequest(false, MedalLink.Require(link)),
        _ => throw new UsageException("The launcher takes no arguments, --at-sign-in, or one medal:// link."),
    };

    public static LauncherStep Decide(bool medalRunning, bool supervisorRunning) =>
        !supervisorRunning ? LauncherStep.Launch : medalRunning ? LauncherStep.Forward : LauncherStep.Busy;

    public static string[] LaunchArguments(LauncherRequest request)
    {
        string[] arguments = ["launch", "--wait-for-server", (request.AtSignIn ? SignInWaitSeconds : ManualWaitSeconds).ToString(CultureInfo.InvariantCulture)];
        return request.Link is null ? arguments : [.. arguments, "--medal-link", MedalLink.Require(request.Link)];
    }

    /// <summary>Repeated sign-in launches never take focus from an existing session.</summary>
    internal static int? HandleExistingSession(LauncherRequest request, bool medalRunning, bool supervisorRunning,
        Action<string?> openMedal, ILauncherUi ui)
    {
        switch (Decide(medalRunning, supervisorRunning))
        {
            case LauncherStep.Forward:
                if (!request.AtSignIn)
                {
                    try { openMedal(request.Link); }
                    catch (MedalSyncException ex) { ui.Show(ex.Message); return 1; }
                }
                return 0;
            case LauncherStep.Busy:
                if (!request.AtSignIn) ui.Show(BusyMessage);
                return 3;
            default:
                return null;
        }
    }

    /// <summary>The message for the user after <c>launch</c> returned, or null when there is nothing to say.</summary>
    public static string? Outcome(int exitCode, string output, string error)
    {
        if (exitCode == 0) return null;
        string last = error.Trim().Length > 0
            ? error.Trim()
            : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        last = StatusPrefix().Replace(last, "");
        last = last.StartsWith("medal-sync: ", StringComparison.Ordinal) ? last["medal-sync: ".Length..] : last;
        string reason = last.Length > 0 ? last : $"medal-sync stopped with exit code {exitCode}; see its log.";
        return exitCode == 3 ? reason : "Medal was not started or its session did not finish.\n\n" + reason;
    }

    [GeneratedRegex("^(launch|finish): [A-Za-z]+: ")]
    private static partial Regex StatusPrefix();

    /// <summary>Explain missing setup before probing Medal; never create state or interrupt sign-in.</summary>
    internal static int? CheckConfiguration(LauncherRequest request, LocalState state, ILauncherUi ui, Action<LocalState>? configure = null)
    {
        if (File.Exists(state.ConfigPath)) return null;
        if (request.AtSignIn) return 1;
        string message = $"Medal Sync needs to be configured before it can open Medal.\n\nOpen Medal Sync Setup Guide from the Start menu for the setup steps. If this PC was configured before, recover its original configuration.\n\nConfiguration location:\n{state.ConfigPath}";
        if (configure is null) ui.Show(message);
        else if (ui.OfferConfiguration(message + "\n\nOpen the configuration wizard now?")) configure(state);
        return 1;
    }

    [SupportedOSPlatform("windows")]
    public static int Run(string[] args, ILauncherUi ui, Action<LocalState>? configure = null)
    {
        LauncherRequest request;
        MedalProcess medal;
        try
        {
            request = Parse(args);
            if (CheckConfiguration(request, LocalState.ForCurrentUser(), ui, configure) is { } setupResult) return setupResult;
            SetupMaintenance.RequireNotRunning();
            medal = new MedalProcess(MedalEnvironment.Locate());
        }
        catch (Exception ex) when (ex is UsageException or MedalSyncException)
        {
            ui.Show(ex.Message);
            return 2;
        }
        var log = new RotatingLog(LocalState.ForCurrentUser().LogDirectory);
        // Record entry before loading the share: this distinguishes a failed
        // sign-in attempt from Windows never invoking the startup entry.
        log.Write(request.AtSignIn ? "launcher: invoked at sign-in" : "launcher: invoked manually");
        // The tray owns no session or mount. It can exit/reconnect without affecting this supervisor.
        try
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, StartupEntries.LauncherFile))
                { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--tray");
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { log.Write("launcher: status view could not be started; supervision continues"); }
        if (HandleExistingSession(request, medal.IsRunning(), SessionCommands.AnotherInstanceRunning(), medal.Open, ui) is { } existingResult)
            return existingResult;
        ReleaseUpdate.NotifyAtLaunch(ui.Notify);
        var shutdown = new SessionShutdown(new SystemClock());
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        bool reviewDeclined = false;
        int code = CommandRunner.Run(LaunchArguments(request), output, error, message => { if (!shutdown.SuppressUi) ui.Notify(message); }, review =>
        {
            if (shutdown.SuppressUi) return false;
            bool accepted = ui.ConfirmLibraryChanges(review) && !shutdown.SuppressUi;
            reviewDeclined = !accepted;
            return accepted;
        }, shutdown);
        // Declining is a deliberate cancellation, not another error dialog.
        if (shutdown.SuppressUi) return code;
        if (reviewDeclined) return 3;
        if (Outcome(code, output.ToString(), error.ToString()) is { } message) ShowOutcome(ui, message, () => medal.IsRunning());
        return code;
    }

    /// <summary>
    /// A dialog takes focus, so it is shown only when Medal (and perhaps a game) has closed. While Medal still runs,
    /// for example after supervision stopped, the outcome is a passive notice in the status view instead.
    /// </summary>
    internal static void ShowOutcome(ILauncherUi ui, string message, Func<bool> medalRunning)
    {
        bool running;
        try { running = medalRunning(); }
        catch (Exception) { running = true; } // Unsure: stay passive.
        if (running) ui.Notify(message);
        else ui.Show(message);
    }
}

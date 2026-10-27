using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LarsonServer.MedalSync.Config;
using LarsonServer.MedalSync.Library;
using LarsonServer.MedalSync.Platform;
using LarsonServer.MedalSync.Session;
using LarsonServer.MedalSync.Util;

namespace LarsonServer.MedalSync.Cli;

/// <summary>How the windowless launcher talks to the user.</summary>
internal interface ILauncherUi
{
    /// <summary>Shows a message and waits until it is closed (used just before the launcher exits).</summary>
    void Show(string message);

    /// <summary>Shows a message without waiting or taking focus (used while Medal runs, perhaps under a game).</summary>
    void Notify(string message);

    /// <summary>Explicit review of every suspect removal, only with Medal stopped.</summary>
    bool ConfirmLibraryChanges(LibraryChangeReview review);
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
    private static readonly TimeSpan LinkStartTimeout = TimeSpan.FromSeconds(60);

    public const string BusyMessage =
        "medal-sync is still finishing the last Medal session (uploading clips). Open Medal again in a minute.";

    public static LauncherRequest Parse(IReadOnlyList<string> args) => args switch
    {
        [] => new LauncherRequest(false, null),
        [StartupEntries.SignInFlag] => new LauncherRequest(true, null),
        [var link] when MedalLink.IsLink(link) => new LauncherRequest(false, MedalLink.Require(link)),
        _ => throw new UsageException("The launcher takes no arguments, --at-sign-in, or one medal:// link."),
    };

    public static LauncherStep Decide(bool medalRunning, bool supervisorRunning) =>
        !supervisorRunning ? LauncherStep.Launch : medalRunning ? LauncherStep.Forward : LauncherStep.Busy;

    public static string[] LaunchArguments(LauncherRequest request) =>
        ["launch", "--wait-for-server", (request.AtSignIn ? SignInWaitSeconds : ManualWaitSeconds).ToString(CultureInfo.InvariantCulture)];

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

    [SupportedOSPlatform("windows")]
    public static int Run(string[] args, ILauncherUi ui)
    {
        LauncherRequest request;
        MedalProcess medal;
        try
        {
            request = Parse(args);
            ReleaseUpdate.NotifyAtLaunch(ui.Notify);
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
        switch (Decide(medal.IsRunning(), SessionCommands.AnotherInstanceRunning()))
        {
            case LauncherStep.Forward:
                medal.Open(request.Link);
                return 0;
            case LauncherStep.Busy:
                ui.Show(BusyMessage);
                return 3;
        }
        var shutdown = new SessionShutdown(new SystemClock());
        if (request.Link is not null) ForwardWhenStarted(medal, request.Link, shutdown);
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
        if (Outcome(code, output.ToString(), error.ToString()) is { } message) ui.Show(message);
        return code;
    }

    /// <summary>Passes a link on once Medal is up; Medal's single instance receives it.</summary>
    [SupportedOSPlatform("windows")]
    private static void ForwardWhenStarted(MedalProcess medal, string link, SessionShutdown shutdown)
    {
        var thread = new Thread(() =>
        {
            var deadline = DateTime.UtcNow + LinkStartTimeout;
            while (DateTime.UtcNow < deadline && !shutdown.SuppressUi)
            {
                if (medal.IsRunning())
                {
                    Thread.Sleep(TimeSpan.FromSeconds(3));
                    try
                    {
                        shutdown.TryLaunch(() => medal.Open(link));
                    }
                    catch (Exception ex) when (ex is MedalSyncException or System.ComponentModel.Win32Exception)
                    {
                        // The link is a convenience; the session itself is unaffected.
                    }
                    return;
                }
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        })
        { IsBackground = true, Name = "medal-link" };
        thread.Start();
    }
}

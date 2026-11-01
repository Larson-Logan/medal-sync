using System.ComponentModel;
using Microsoft.Win32;
using MedalSync.Launch;
using MedalSync.Platform;
using MedalSync.Session;

internal static class UpdateTests
{
    internal static void Run(Action<bool, string> check)
    {
        string setup = Path.Combine(Path.GetTempPath(), "synthetic-download", "setup.exe");
        var actions = new List<string>();
        Func<Func<UpdateReadiness>, bool> immediate = readiness => readiness() == UpdateReadiness.Ready;
        int code = WindowsReleaseUpdate.InstallWhenReady("2.0.0.00", setup, () => UpdateReadiness.Ready, immediate,
            () => actions.Add("close"), path => actions.Add("start:" + path), _ => actions.Add("message"), path => actions.Add("folder:" + path));
        check(code == 0 && actions.SequenceEqual(["close", "start:" + setup]), "A Ready library closes the status app and starts the verified installer without another prompt");

        actions.Clear();
        string? postponed = null;
        code = WindowsReleaseUpdate.InstallWhenReady("2.0.0.00", setup, () => UpdateReadiness.MedalRunning, _ => false,
            () => actions.Add("close"), _ => actions.Add("start"), message => postponed = message, path => actions.Add("folder:" + path));
        check(code == 0 && actions.Count == 0 && postponed!.Contains("without downloading again", StringComparison.Ordinal),
            "Canceling the wait leaves Medal Sync running and keeps the installer for later");

        foreach (string failureKind in new[] { "blocked", "busy", "offline", "setup" })
        {
            actions.Clear();
            string? diagnostic = null;
            code = WindowsReleaseUpdate.InstallWhenReady("2.0.0.00", setup, () => UpdateReadiness.Ready,
                readiness => failureKind == "blocked" ? throw new MedalSync.UnsafeOperationException("The shared library is not Ready.") : readiness() == UpdateReadiness.Ready,
                () =>
                {
                    actions.Add("close");
                    if (failureKind == "busy") throw new IOException("Medal Sync could not exit.");
                    if (failureKind == "offline") throw new UnauthorizedAccessException("Storage is offline");
                },
                _ => { actions.Add("start"); throw new Win32Exception(1223); },
                message => { diagnostic = message; actions.Add("message"); }, _ => actions.Add("folder"));
            check(code == 1 && diagnostic!.Contains(setup, StringComparison.Ordinal) && actions.Last() == "folder",
                "A blocked library, exit or canceled setup keeps a clear path to retry the downloaded installer");
            check(actions.Contains("start") == (failureKind == "setup"), "A failed readiness or exit check never starts setup");
            check(actions.Contains("close") == (failureKind != "blocked"), "A library that is not Ready leaves the status app open");
        }

        Wait(check);
        Arguments(check);
        CloseStatus(check);
    }

    private static void Wait(Action<bool, string> check)
    {
        // Medal open, then supervision finishing, then Ready: each state is reported once and never ends a process.
        var states = new Queue<UpdateReadiness>([UpdateReadiness.MedalRunning, UpdateReadiness.MedalRunning,
            UpdateReadiness.Finishing, UpdateReadiness.Ready]);
        var reported = new List<UpdateReadiness>();
        int sleeps = 0;
        var clock = DateTimeOffset.UnixEpoch;
        bool ready = WindowsReleaseUpdate.WaitUntilReady(states.Dequeue, reported.Add, () => false,
            interval => { sleeps++; clock += interval; }, () => clock);
        check(ready && sleeps == 3 && reported.SequenceEqual([UpdateReadiness.MedalRunning, UpdateReadiness.Finishing]),
            "The update waits for Medal to quit and supervision to finish, reporting each change once");

        int polls = 0;
        bool canceled = WindowsReleaseUpdate.WaitUntilReady(() => { polls++; return UpdateReadiness.MedalRunning; }, _ => { },
            () => polls >= 2, _ => { }, () => clock);
        check(!canceled && polls == 2, "Cancel stops waiting without installing");

        // A refusal right after supervision exits is retried while the handoff completes, then succeeds.
        int attempts = 0;
        ready = WindowsReleaseUpdate.WaitUntilReady(() => ++attempts < 3 ? throw new MedalSync.UnsafeOperationException("Not Ready yet") : UpdateReadiness.Ready,
            _ => { }, () => false, interval => clock += interval, () => clock, TimeSpan.FromSeconds(5));
        check(ready && attempts == 3, "A brief refusal during the final handoff is retried");

        // A lasting refusal is reported with its own guidance after the grace period.
        string? refusal = null;
        try
        {
            WindowsReleaseUpdate.WaitUntilReady(() => throw new MedalSync.UnsafeOperationException("An unfinished local session is retained."),
                _ => { }, () => false, interval => clock += interval, () => clock, TimeSpan.FromSeconds(3));
        }
        catch (MedalSync.UnsafeOperationException ex) { refusal = ex.Message; }
        check(refusal == "An unfinished local session is retained.", "A lasting refusal stops waiting and explains what to fix");

        check(WindowsReleaseUpdate.WaitText(UpdateReadiness.MedalRunning, "2.0.0.00").Title.Contains("quit Medal", StringComparison.Ordinal)
            && WindowsReleaseUpdate.WaitText(UpdateReadiness.Finishing, "2.0.0.00").Detail.Contains("2.0.0.00", StringComparison.Ordinal),
            "The waiting window says what the user needs to do and which version installs");
    }

    private static void Arguments(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "synthetic-installs");
        string user = Path.Combine(root, "Users", "Someone", "AppData", "Local", "Programs");
        string machine = Path.Combine(root, "Program Files");
        string perUser = Path.Combine(user, "Medal Sync");
        string allUsers = Path.Combine(machine, "Medal Sync");
        Func<RegistryHive, string?> none = _ => null;
        check(WindowsReleaseUpdate.InstallMode(perUser, hive => hive == RegistryHive.CurrentUser ? perUser + "\\" : null, machine, user) == "/CURRENTUSER",
            "A per-user registration upgrades per-user");
        check(WindowsReleaseUpdate.InstallMode(perUser, hive => hive == RegistryHive.LocalMachine ? perUser : null, Path.Combine(root, "elsewhere"), Path.Combine(root, "other")) == "/ALLUSERS",
            "An all-users registration upgrades all-users");
        check(WindowsReleaseUpdate.InstallMode(perUser, none, machine, user) == "/CURRENTUSER"
            && WindowsReleaseUpdate.InstallMode(allUsers, none, machine, user) == "/ALLUSERS",
            "Without a registration the standard program location decides the install mode");
        check(WindowsReleaseUpdate.InstallMode(Path.Combine(root, "Portable"), none, machine, user) is null
            && WindowsReleaseUpdate.InstallMode(Path.Combine(root, "Program Files Old", "Medal Sync"), none, machine, user) is null,
            "An unknown location keeps the interactive setup wizard");

        string? arguments = WindowsReleaseUpdate.SetupArguments("/CURRENTUSER", perUser + "\\");
        check(arguments == $"/SILENT /NORESTART /CURRENTUSER /DIR=\"{perUser}\" /MEDALSYNCRELAUNCH=status",
            "An update upgrades the same folder unattended and asks setup to reopen the status app");
        check(WindowsReleaseUpdate.SetupArguments(null, perUser) is null, "An unknown install mode opens the normal wizard");
        check(WindowsReleaseUpdate.ProgramDirectory(Path.Combine(perUser, "bin") + "\\") == perUser
            && WindowsReleaseUpdate.ProgramDirectory(Path.Combine(root, "dev", "publish")) is null,
            "Only an installed bin folder is upgraded unattended");
    }

    private static void CloseStatus(Action<bool, string> check)
    {
        string name = @"Local\MedalSync.UpdateTest." + Guid.NewGuid().ToString("N");
        string eventName = name + ".Open";
        using var started = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var singleton = new Mutex(true, name);
                try
                {
                    using var view = new WindowsTrayStatus(false,
                        () => new DesktopStatus(SessionPhase.Ready, "Synthetic Ready", DateTimeOffset.UtcNow),
                        eventName, operationRunning: () => false);
                    started.Set();
                    Application.Run(view);
                }
                finally { singleton.ReleaseMutex(); }
            }
            catch (Exception ex) { failure = ex; started.Set(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        check(started.Wait(TimeSpan.FromSeconds(10)), "Synthetic status app starts");
        if (failure is not null) throw failure;
        WindowsTrayStatus.CloseForUpdate(name, eventName);
        check(thread.Join(TimeSpan.FromSeconds(5)) && failure is null, "Update handoff closes and disposes the status app without terminating a process");
        WindowsTrayStatus.CloseForUpdate(name, eventName);
        check(true, "Updating with no running status app is supported");

        // A view that cannot acknowledge exit must block setup, without ending another process.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var busy = new Thread(() =>
        {
            using var singleton = new Mutex(true, name);
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName + ".Exit");
            held.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            singleton.ReleaseMutex();
        }) { IsBackground = true };
        busy.Start();
        check(held.Wait(TimeSpan.FromSeconds(5)), "Synthetic unresponsive status app starts");
        try
        {
            try { WindowsTrayStatus.CloseForUpdate(name, eventName, TimeSpan.FromMilliseconds(50)); throw new InvalidOperationException("Exit should time out."); }
            catch (IOException ex) { check(ex.Message.Contains("could not exit", StringComparison.Ordinal), "An unresponsive status app blocks installer launch with recovery instructions"); }
        }
        finally { release.Set(); busy.Join(TimeSpan.FromSeconds(5)); }
    }
}

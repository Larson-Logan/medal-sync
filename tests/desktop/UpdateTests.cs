using System.ComponentModel;
using MedalSync.Launch;
using MedalSync.Platform;
using MedalSync.Session;

internal static class UpdateTests
{
    internal static void Run(Action<bool, string> check)
    {
        string setup = Path.Combine(Path.GetTempPath(), "synthetic-download", "setup.exe");
        var actions = new List<string>();
        int code = WindowsReleaseUpdate.OfferInstall("2.0.0.00", setup,
            message => message.Contains("Install the update now?", StringComparison.Ordinal),
            _ => actions.Add("message"), () => actions.Add("prepare"),
            path => actions.Add("start:" + path), path => actions.Add("folder:" + path));
        check(code == 0 && actions.SequenceEqual(["prepare", "start:" + setup]), "Accepting installation prepares exit before starting the verified installer");

        actions.Clear();
        code = WindowsReleaseUpdate.OfferInstall("2.0.0.00", setup, _ => false,
            _ => actions.Add("message"), () => actions.Add("prepare"),
            _ => actions.Add("start"), path => actions.Add("folder:" + path));
        check(code == 0 && actions.SequenceEqual(["folder:" + Path.GetDirectoryName(setup)]), "Deferring installation leaves the app running and opens the saved installer folder");

        foreach (string failureKind in new[] { "busy", "offline", "setup" })
        {
            actions.Clear();
            string? diagnostic = null;
            code = WindowsReleaseUpdate.OfferInstall("2.0.0.00", setup, _ => true,
                message => { diagnostic = message; actions.Add("message"); },
                () =>
                {
                    actions.Add("prepare");
                    if (failureKind == "busy") throw new MedalSync.UnsafeOperationException("Still uploading");
                    if (failureKind == "offline") throw new IOException("Storage is offline");
                },
                _ => { actions.Add("start"); throw new Win32Exception(1223); },
                _ => actions.Add("folder"));
            check(code == 1 && diagnostic!.Contains(setup, StringComparison.Ordinal) && actions.Last() == "folder",
                "A blocked exit or canceled setup keeps a clear path to retry the downloaded installer");
            check(actions.Contains("start") == (failureKind == "setup"), "A failed readiness or exit check never starts setup");
        }

        CloseStatus(check);
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

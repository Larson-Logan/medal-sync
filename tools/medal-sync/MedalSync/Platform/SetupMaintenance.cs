using System.Diagnostics;
using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;

namespace MedalSync.Platform;

internal static class SetupMaintenance
{
    // Retained so current and older installers cannot mutate an installation concurrently.
    internal const string MutexName = @"Global\LarsonServer.MedalSync.Setup";

    public static void RequireNotRunning()
    {
        if (OperatingSystem.IsWindows()) RequireNotRunning(MutexName);
    }

    internal static void RequireNotRunning(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) return;
            mutex.Dispose();
        }
        catch (UnauthorizedAccessException) { /* Another user/elevated setup owns it. */ }
        throw new UnsafeOperationException("Medal Sync setup or uninstall is open. Finish or cancel it before starting a library operation.");
    }

    internal static void RequireReady(string root, bool applicationRunning, bool supervisorRunning,
        Func<MedalSyncConfig, OwnershipRecord>? readJournal = null)
    {
        if (applicationRunning || supervisorRunning) throw new UnsafeOperationException("Quit Medal normally and wait for Ready, then choose Exit Medal Sync in the status panel or tray menu before installing or uninstalling. Older versions call this Exit status view (supervision continues).");
        if (Directory.Exists(Path.Combine(root, "sessions")) && Directory.EnumerateFiles(Path.Combine(root, "sessions"), "*.json").Any())
            throw new UnsafeOperationException("An unfinished local session is retained. Cancel setup, recover the session and wait for Ready, then run setup again.");
        bool config = File.Exists(Path.Combine(root, "config.json"));
        bool device = File.Exists(Path.Combine(root, "device.json"));
        if (!config && !device) return;
        if (!config || !device) throw new UnsafeOperationException("The existing setup has incomplete configuration or identity. Recover that installation before continuing.");
        var state = new LocalState(root);
        var settings = MedalSyncConfig.Load(state.ConfigPath);
        _ = state.ExistingDevice(); // Strictly read-only, including a concurrent missing-file race.
        var journal = readJournal is not null ? readJournal(settings)
            : CoordinationStore.Open(settings).ReadJournal();
        if (journal.State != OwnershipState.Clean || journal.Owner is not null || journal.SessionId is not null)
            throw new UnsafeOperationException("The shared library is not Ready. Cancel setup, finish its session and wait for Ready, then run setup again.");
    }

    [SupportedOSPlatform("windows")]
    internal static bool MedalRunning() => AnyProcessRunning(["Medal", "MedalEncoder", "MedalRecorder"]);

    [SupportedOSPlatform("windows")]
    internal static bool ApplicationRunning() => AnyProcessRunning(["Medal", "MedalEncoder", "MedalRecorder", "medal-sync", "medal-sync-launch"]);

    [SupportedOSPlatform("windows")]
    private static bool AnyProcessRunning(string[] names)
    {
        foreach (string name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process) if (process.Id != Environment.ProcessId) return true;
            }
        }
        return false;
    }
}

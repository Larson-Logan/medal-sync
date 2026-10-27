using System.Diagnostics;
using System.Runtime.Versioning;
using LarsonServer.MedalSync.Config;
using LarsonServer.MedalSync.Coordination;

namespace LarsonServer.MedalSync.Platform;

internal static class SetupMaintenance
{
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
        if (applicationRunning || supervisorRunning) throw new UnsafeOperationException("Quit Medal normally and wait for Ready before installing or uninstalling Medal Sync.");
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
            : CoordinationStore.Open(settings.LibraryCoordinationDirectory, settings.LibraryId, settings.AccountId).ReadJournal();
        if (journal.State != OwnershipState.Clean || journal.Owner is not null || journal.SessionId is not null)
            throw new UnsafeOperationException("The shared library is not Ready. Cancel setup, finish its session and wait for Ready, then run setup again.");
    }

    [SupportedOSPlatform("windows")]
    internal static bool ApplicationRunning()
    {
        foreach (string name in new[] { "Medal", "MedalEncoder", "MedalRecorder", "medal-sync", "medal-sync-launch" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process) if (process.Id != Environment.ProcessId) return true;
            }
        }
        return false;
    }
}

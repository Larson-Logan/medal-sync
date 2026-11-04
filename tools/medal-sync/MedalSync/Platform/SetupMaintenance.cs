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

    // Held by the status view (medal-sync-launch --tray), which stays open after Medal and supervision stop.
    internal const string StatusViewMutexName = @"Local\LarsonServer.MedalSync.StatusView";

    internal const string ApplicationRunningMessage = "Quit Medal normally and wait for Ready, then choose Exit Medal Sync in the status panel or tray menu before installing or uninstalling. Older versions call this Exit status view (supervision continues).";

    internal const string StatusViewOpenMessage = "Only the Medal Sync status view is still open. Choose Exit Medal Sync in the status panel or tray menu, then try again. Older versions call this Exit status view (supervision continues).";

    internal static void RequireReady(string root, bool applicationRunning, bool supervisorRunning,
        Func<MedalSyncConfig, OwnershipRecord>? readJournal = null) =>
        RequireReady(root, applicationRunning ? RunningApplications.Other : RunningApplications.None, supervisorRunning, readJournal);

    internal static void RequireReady(string root, RunningApplications applications, bool supervisorRunning,
        Func<MedalSyncConfig, OwnershipRecord>? readJournal = null)
    {
        if (applications == RunningApplications.Other || supervisorRunning) throw new UnsafeOperationException(ApplicationRunningMessage);
        if (applications == RunningApplications.StatusViewOnly) throw new UnsafeOperationException(StatusViewOpenMessage);
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

    /// <summary>
    /// What setup would have to wait for. The status view alone is told apart so setup can say to exit it,
    /// rather than to quit Medal, which is already closed.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static RunningApplications RunningApplication() =>
        Classify(AnyProcessRunning(["Medal", "MedalEncoder", "MedalRecorder", "medal-sync"]), CountProcesses("medal-sync-launch"), StatusViewOpen);

    /// <summary>A single launcher process is the status view only while the view's mutex exists; a supervisor also runs as a launcher.</summary>
    internal static RunningApplications Classify(bool otherRunning, int launchers, Func<bool> statusViewOpen)
    {
        if (otherRunning || launchers > 1) return RunningApplications.Other;
        if (launchers == 0) return RunningApplications.None;
        return statusViewOpen() ? RunningApplications.StatusViewOnly : RunningApplications.Other;
    }

    [SupportedOSPlatform("windows")]
    private static bool StatusViewOpen()
    {
        try
        {
            if (!Mutex.TryOpenExisting(StatusViewMutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; /* It exists; another security context created it. */ }
    }

    [SupportedOSPlatform("windows")]
    private static bool AnyProcessRunning(string[] names) => names.Any(name => CountProcesses(name) > 0);

    [SupportedOSPlatform("windows")]
    private static int CountProcesses(string name)
    {
        int count = 0;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process) if (process.Id != Environment.ProcessId) count++;
        }
        return count;
    }
}

/// <summary>Programs that block setup, other than setup itself.</summary>
internal enum RunningApplications { None, StatusViewOnly, Other }

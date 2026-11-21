using System.Diagnostics;
using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Util;

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
        if (!config) throw new UnsafeOperationException(IdentityWithoutConfigurationMessage);
        if (!device)
        {
            RequireNotEnrolled(root);
            return;
        }
        var state = new LocalState(root);
        var settings = MedalSyncConfig.Load(state.ConfigPath);
        var thisPc = state.ExistingDevice(); // Strictly read-only, including a concurrent missing-file race.
        // Older init previews could leave an identity before creating a library. Check that storage can
        // confirm the library is absent and still refuse if this PC has session recovery evidence.
        if (LibrarySetupPending.Read(state, settings)?.AwaitsInitialization(state, settings, () => false) == true
            && (settings.StorageMode == StorageMode.Cloud ? !CloudJournalProvider.For(settings).Exists()
                : CoordinationDirectoryMissing(settings)))
        {
            RequireNoRecoveryEvidence(root, settings);
            return;
        }
        var journal = readJournal is not null ? readJournal(settings)
            : CoordinationStore.Open(settings).ReadJournal();
        if (journal.State == OwnershipState.Clean && journal.Owner is null && journal.SessionId is null) return;
        if (journal.State == OwnershipState.Migrated) return; // The former journal can no longer grant a session.
        // Setup changes only this PC's programs, never the journal, cache or library, so another PC's session
        // cannot be harmed by it. This PC's own retained login was refused above; its ownership is refused here.
        if (journal.State != OwnershipState.Clean && journal.SessionId is not null
            && journal.Owner is { } owner && owner.DeviceId != thisPc.DeviceId) return;
        throw new UnsafeOperationException(journal.Owner?.DeviceId == thisPc.DeviceId ? HeldByThisPcMessage : NotReadyMessage);
    }

    internal const string HeldByThisPcMessage = "This PC holds the shared library, so it is not Ready. Cancel setup, finish this PC's session and wait for Ready, then run setup again.";

    internal const string NotReadyMessage = "The shared library is not Ready. Cancel setup, finish its session and wait for Ready, then run setup again.";

    internal const string IdentityWithoutConfigurationMessage = "This PC has a Medal Sync device identity (device.json) but no configuration (config.json). Recover that installation before continuing.";

    /// <summary>
    /// A saved wizard configuration that no init or enroll has used yet. Without a device identity this PC cannot own
    /// the library, so there is nothing to wait for, provided no session evidence or session cache exists either.
    /// </summary>
    private static void RequireNotEnrolled(string root)
    {
        MedalSyncConfig settings;
        try { settings = MedalSyncConfig.Load(Path.Combine(root, "config.json")); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or MedalSyncException)
        {
            throw new UnsafeOperationException("The saved configuration (config.json) cannot be read and this PC has no device identity yet. "
                + "Restore the saved configuration before opening the configuration wizard. If library-setup.json exists, keep it with its matching configuration.");
        }
        RequireNoRecoveryEvidence(root, settings);
    }

    private static bool CoordinationDirectoryMissing(MedalSyncConfig settings)
    {
        try { _ = File.GetAttributes(settings.LibraryCoordinationDirectory); }
        catch (FileNotFoundException) { return Directory.Exists(settings.CoordinationRoot); }
        catch (DirectoryNotFoundException) { return Directory.Exists(settings.CoordinationRoot); }
        return false;
    }

    private static void RequireNoRecoveryEvidence(string root, MedalSyncConfig settings)
    {
        foreach (string evidence in new[] { "aborted-starts", "finished-sessions" })
            if (HasEntries(Path.Combine(root, evidence)))
                throw new UnsafeOperationException($"Session recovery evidence ({evidence}) is retained. Recover that installation before continuing.");
        if (HasEntries(WindowsPath.Parse(settings.CacheRoot).Append(settings.LibraryId).Display))
            throw new UnsafeOperationException("A session cache for this library is retained. Recover that installation before continuing.");
    }

    private static bool HasEntries(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();

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

using System.Data.Common;
using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>
/// Gathers <see cref="DiagnosticFacts"/> on this PC. Observational only: no restore, mount, ownership change,
/// device identity, request or credential is created. Each part that cannot be read is recorded as unavailable.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DiagnosticCollector
{
    public static DiagnosticFacts Collect(LocalState state, DateTimeOffset now, Func<bool> supervisorRunning)
    {
        var facts = new DiagnosticFacts
        {
            Now = now,
            ToolVersion = ToolInfo.Version,
            WindowsVersion = Environment.OSVersion.Version.ToString(),
            Private = [Environment.MachineName, Environment.UserName, Environment.UserDomainName, state.Root, AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), MedalEnvironment.InstallDirectory, MedalEnvironment.DataDirectory],
        };
        try { facts = facts with { SetupInstall = LocalState.IsManagedInstall(AppContext.BaseDirectory) }; }
        catch (Exception ex) when (Expected(ex)) { facts = facts with { InstallError = ex }; }

        MedalInstall? install = null;
        try
        {
            install = MedalEnvironment.Locate();
            facts = facts with
            {
                SignedInAccount = install.AccountId,
                Compatibility = MedalCompatibility.Check(install),
                MedalRunning = new MedalProcess(install).IsRunning(),
                Private = [.. facts.Private, install.DatabasePath, install.Executable],
            };
            using var connection = LibraryDatabase.OpenReadOnly(install.DatabasePath);
            facts = facts with
            {
                BitrateMbps = double.TryParse(LibraryDatabase.ReadSetting(connection, null, "Bitrate"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double bitrate) && bitrate > 0 ? bitrate : null,
            };
        }
        catch (Exception ex) when (Expected(ex))
        {
            if (install is null) facts = facts with { MedalError = ex };
        }

        if (File.Exists(state.ConfigPath))
        {
            facts = facts with { ConfigPresent = true };
            try { facts = facts with { Config = MedalSyncConfig.Load(state.ConfigPath) }; }
            catch (Exception ex) when (Expected(ex)) { facts = facts with { ConfigError = ex }; }
        }
        var config = facts.Config;

        try
        {
            string rclone = config?.ExpandedRclonePath ?? Cli.ConfigCommands.DefaultRclonePath();
            bool present = File.Exists(rclone);
            facts = facts with
            {
                RclonePresent = present,
                RcloneSha256 = present ? Hashing.Sha256File(rclone) : null,
                WinFspInstalled = Inventory.WinFspDirectory() is not null,
            };
        }
        catch (Exception ex) when (Expected(ex)) { facts = facts with { DependencyError = ex }; }

        if (config is null) return facts;
        try
        {
            var volume = StorageVolume.Describe(config.CacheRoot);
            facts = facts with { CacheFreeBytes = volume?.FreeBytes, CacheTotalBytes = volume?.TotalBytes };
            // The same admission launch uses: the cache and any persistent clip folder, sharing one budget on one disk.
            if (facts.BitrateMbps is { } bitrate)
                facts = facts with
                {
                    SessionFits = CapacityAdmission.ForRecording(config, config.CacheRoot, bitrate, 0, StorageVolume.Describe).All(v => v.Admitted),
                };
        }
        catch (Exception ex) when (Expected(ex)) { facts = facts with { CapacityError = ex }; }

        facts = facts with
        {
            PendingCloudCopies = StorageReplication.Pending(state, config.LibraryId).Select(p => p.Name).ToArray(),
            LastVerifiedAt = StatusReport.ReadLastVerifiedAt(state.Root, config.LibraryId),
        };
        // Like status: a PC that never started a session has no identity, and none is created here.
        DeviceIdentity? device = null;
        try { device = File.Exists(Path.Combine(state.Root, "device.json")) ? state.ExistingDevice() : null; }
        catch (Exception ex) when (Expected(ex)) { /* Reported as no identity; ownership is still read. */ }
        facts = facts with { Device = device };
        try
        {
            if (config.StorageMode != StorageMode.Cloud && !Directory.Exists(config.CoordinationRoot)) return facts with { CoordinationReachable = false };
            facts = facts with { CoordinationReachable = true };
            var store = CoordinationStore.Open(config);
            var journal = store.ReadJournal();
            facts = facts with
            {
                Journal = journal,
                PendingSwitchRequests = store.PendingRequests(now).Count,
                Phase = DesktopStatus.Read(state, config.LibraryId, journal, device, supervisorRunning(), now).Phase,
            };
        }
        catch (Exception ex) when (Expected(ex)) { facts = facts with { JournalError = ex }; }
        return facts;
    }

    private static bool Expected(Exception ex) => ex is MedalSyncException or IOException or UnauthorizedAccessException
        or InvalidDataException or DbException or System.Xml.XmlException or System.Security.SecurityException or ArgumentException;
}

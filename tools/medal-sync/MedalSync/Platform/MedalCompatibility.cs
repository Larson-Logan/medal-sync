using System.Data.Common;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Session;

namespace MedalSync.Platform;

/// <summary>Only build and schema metadata: no library rows or device settings.</summary>
internal sealed record MedalCompatibilityReport(
    MedalVersions Versions,
    bool ExecutablePresent,
    long? SchemaVersion,
    long? DefinitionsVersion,
    string? SchemaFingerprint,
    bool SchemaSupported,
    IReadOnlyList<string> Blockers)
{
    public bool Compatible => Blockers.Count == 0;

    public string Summary => $"Medal app {Versions.App ?? "unknown"} / recorder {Versions.Recorder ?? "unknown"}: "
        + (Compatible ? "compatible with this Medal Sync build." : string.Join(" ", Blockers));

    public void RequireCompatible()
    {
        if (!Compatible)
            throw new IncompatibleLibraryException(Summary + " Run 'medal-sync compatibility --json' for details and check for a Medal Sync update. "
                + "Keep unfinished sessions and caches on this PC.");
    }
}

internal static class MedalCompatibility
{
    public static MedalCompatibilityReport Check(MedalInstall install)
    {
        // Never use the cached versions from Locate: Medal can update while
        // waiting for storage or while the current session is running.
        var versions = MedalEnvironment.ReadVersions(install.InstallDirectory);
        bool executable = File.Exists(install.Executable);
        var blockers = new List<string>();
        if (!executable) blockers.Add("Medal's executable is missing; let its update finish or repair Medal before retrying.");
        if (!NativeBackupBuilds.IsSupported(versions))
            blockers.Add("This app/recorder pair has not been verified for Medal's native database backup behavior.");
        SchemaInfo? schema = null;
        try
        {
            using var connection = LibraryDatabase.OpenReadOnly(install.DatabasePath);
            using var transaction = connection.BeginTransaction(deferred: true);
            schema = MedalSchema.Read(connection, transaction);
            if (!schema.IsSupported)
                blockers.Add($"Unsupported library schema: user_version {schema.UserVersion}, definitions {schema.DefinitionsVersion?.ToString() ?? "missing"}, fingerprint {schema.Fingerprint}.");
        }
        catch (Exception ex) when (ex is MedalSyncException or IOException or UnauthorizedAccessException or InvalidDataException or DbException)
        {
            blockers.Add("The library schema could not be checked: " + ex.Message);
        }
        return new MedalCompatibilityReport(versions, executable, schema?.UserVersion, schema?.DefinitionsVersion,
            schema?.Fingerprint, schema?.IsSupported == true, blockers);
    }
}

/// <summary>Fresh metadata at startup, during supervision and before publication.</summary>
internal sealed class MedalCompatibilityGuard(MedalInstall install) : ISessionCompatibility
{
    public void RequireLaunch()
    {
        var report = MedalCompatibility.Check(install);
        report.RequireCompatible();
        if (report.Versions != install.Versions)
            throw new IncompatibleLibraryException("Medal updated while launch was being prepared. Retry through Medal Sync so its new versions are detected.");
    }

    public MedalVersions RequirePublish()
    {
        var report = MedalCompatibility.Check(install);
        report.RequireCompatible();
        return report.Versions;
    }

    public string? CheckForUpdate()
    {
        var report = MedalCompatibility.Check(install);
        if (report.Compatible && report.Versions == install.Versions) return null;
        return (report.Compatible ? "Medal update detected. " : "Medal compatibility problem detected. ") + report.Summary
            + (report.Compatible ? " Quit Medal normally to finish the session."
                : " Quit Medal normally; uploads can drain, but handoff is blocked. Keep this PC's session and cache, run 'medal-sync compatibility --json', and check for a Medal Sync update.");
    }
}

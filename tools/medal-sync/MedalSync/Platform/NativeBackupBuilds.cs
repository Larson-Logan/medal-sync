using MedalSync.Coordination;

namespace MedalSync.Platform;

/// <summary>Exact app/recorder pairs whose native database restore and lineage behavior were reviewed.</summary>
internal static class NativeBackupBuilds
{
    public static bool IsSupported(MedalVersions versions) =>
        versions.Recorder == "2643.3040.1" && versions.App is "2643.583.1" or "2644.591.1";

    public static void RequireSupported(MedalVersions versions)
    {
        // Both app builds restore a nonzero, mismatched application_id backup in full.
        // Matching lineage suppresses that restore; ClipFolders is read before database open.
        if (!IsSupported(versions))
            throw new IncompatibleLibraryException($"Medal app {versions.App ?? "unknown"} / recorder {versions.Recorder ?? "unknown"} needs native backup compatibility verification. "
                + "Run 'medal-sync compatibility --json' for details and check for a Medal Sync update. Keep unfinished sessions and caches on this PC.");
    }

    public static void RequireCompatible(MedalVersions shared, MedalVersions local)
    {
        // These reviewed builds share the same backup behavior. The library
        // reconciler separately requires an identical supported schema.
        RequireSupported(shared);
        RequireSupported(local);
    }
}

using MedalSync.Coordination;

namespace MedalSync.Platform;

/// <summary>Exact app/recorder pairs whose native database restore and lineage behavior were reviewed.</summary>
internal static class NativeBackupBuilds
{
    public static void RequireSupported(MedalVersions versions)
    {
        // Both app builds restore a nonzero, mismatched application_id backup in full.
        // Matching lineage suppresses that restore; ClipFolders is read before database open.
        if (versions.Recorder != "2643.3040.1" || versions.App is not ("2643.583.1" or "2644.591.1"))
            throw new IncompatibleLibraryException("Medal's native backup compatibility needs verification for this application/recorder build.");
    }
}

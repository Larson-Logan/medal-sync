using MedalSync.Coordination;

namespace MedalSync.Platform;

/// <summary>Exact app/recorder pairs whose native database restore and lineage behavior were reviewed.</summary>
internal static class NativeBackupBuilds
{
    // Each pair was checked against that installed build. Never add a version
    // range or an app build with a recorder it was not reviewed with.
    private static readonly HashSet<(string App, string Recorder)> Reviewed =
    [
        ("2643.583.1", "2643.3040.1"),
        ("2644.591.1", "2643.3040.1"),
        ("2644.604.1", "2644.3209.1"),
    ];

    public static bool IsSupported(MedalVersions versions) =>
        versions.App is { } app && versions.Recorder is { } recorder && Reviewed.Contains((app, recorder));

    public static void RequireSupported(MedalVersions versions)
    {
        // Every reviewed app build restores a nonzero, mismatched application_id backup in full.
        // Matching lineage suppresses that restore; ClipFolders is read before database open.
        // The reviewed recorders open only their own game-info databases, never the library.
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

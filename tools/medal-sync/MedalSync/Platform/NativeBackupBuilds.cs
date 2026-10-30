using MedalSync.Coordination;

namespace MedalSync.Platform;

/// <summary>Historical build reviews are informational; schema and backup checks decide admission.</summary>
internal static class NativeBackupBuilds
{
    // Keep the distinction between a reviewed pair and an automatically admitted
    // update visible in diagnostics. Version numbers are not a schema contract.
    private static readonly HashSet<(string App, string Recorder)> Reviewed =
    [
        ("2643.583.1", "2643.3040.1"),
        ("2644.591.1", "2643.3040.1"),
        ("2644.604.1", "2644.3209.1"),
    ];

    public static bool IsReviewed(MedalVersions versions) =>
        versions.App is { } app && versions.Recorder is { } recorder && Reviewed.Contains((app, recorder));

    public static bool HasReadableVersions(MedalVersions versions) =>
        Readable(versions.App) && Readable(versions.Recorder);

    private static bool Readable(string? value) =>
        value is not null && value.Length <= 64 && value.All(c => char.IsAsciiDigit(c) || c == '.')
        && Version.TryParse(value, out var version) && version.Build >= 0;

    public static void RequireVersionMetadata(MedalVersions versions)
    {
        if (!HasReadableVersions(versions))
            throw new IncompatibleLibraryException("Medal's app or recorder version metadata is missing or unreadable. Let its update finish, then retry through Medal Sync. Keep unfinished sessions and caches on this PC.");
    }

    public static void RequireCompatible(MedalVersions shared, MedalVersions local)
    {
        // Enrollment separately compares the exact supported schemas. A version
        // difference by itself does not mean the two library formats differ.
        RequireVersionMetadata(shared);
        RequireVersionMetadata(local);
    }
}

using System.Xml.Linq;
using MedalSync.Config;

namespace MedalSync.Platform;

internal sealed record SyncFolder(string Profile, string Id, string Label, string Path, string Type);

internal sealed record ExclusionStatus(SyncExclusion Exclusion, SyncFolder? Folder, bool Present, string Detail);

/// <summary>
/// Checks that every Syncthing folder overlapping the clip destination ignores
/// it on this PC. Only reads configuration; never edits it.
/// </summary>
internal static class SyncthingCheck
{
    /// <summary>
    /// The profiles the Larson Drive setup starts at sign-in: the primary
    /// profile and an additional PC's profile. Others (such as the 2026-10-08
    /// conflict-test profiles) are listed but not checked.
    /// </summary>
    public static readonly IReadOnlySet<string> ActiveProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "drive-sync", "drive-sync-remote" };

    /// <summary>Folders of every Larson Drive profile under <paramref name="larsonServerDirectory"/>.</summary>
    public static IReadOnlyList<SyncFolder> Folders(string larsonServerDirectory)
    {
        if (!Directory.Exists(larsonServerDirectory)) return [];
        var folders = new List<SyncFolder>();
        foreach (string profile in Directory.EnumerateDirectories(larsonServerDirectory, "drive-sync*"))
        {
            string config = System.IO.Path.Combine(profile, "config", "config.xml");
            if (!File.Exists(config)) continue;
            var document = XDocument.Load(config);
            foreach (var folder in document.Root?.Elements("folder") ?? [])
            {
                folders.Add(new SyncFolder(System.IO.Path.GetFileName(profile), (string?)folder.Attribute("id") ?? "",
                    (string?)folder.Attribute("label") ?? "", (string?)folder.Attribute("path") ?? "", (string?)folder.Attribute("type") ?? ""));
            }
        }
        return folders;
    }

    public static IReadOnlyList<ExclusionStatus> Check(IReadOnlyList<SyncFolder> folders, IEnumerable<SyncExclusion> exclusions)
    {
        var results = new List<ExclusionStatus>();
        foreach (var exclusion in exclusions)
        {
            var matches = folders.Where(f => f.Id == exclusion.FolderId && ActiveProfiles.Contains(f.Profile)).ToList();
            if (matches.Count == 0)
            {
                results.Add(new ExclusionStatus(exclusion, null, true, "folder not configured on this PC"));
                continue;
            }
            foreach (var folder in matches)
            {
                string ignore = System.IO.Path.Combine(folder.Path, ".stignore");
                bool present = File.Exists(ignore) && File.ReadLines(ignore).Any(l => l.Trim() == exclusion.Pattern);
                results.Add(new ExclusionStatus(exclusion, folder, present,
                    present ? $"{ignore} ignores {exclusion.Pattern}" : $"{ignore} does not ignore {exclusion.Pattern}"));
            }
        }
        return results;
    }
}

using System.Text;

namespace MedalSync.Platform;

/// <summary>Human-readable form of the inventory, with blockers called out.</summary>
internal static class InventoryText
{
    public static string Render(InventoryReport r)
    {
        var text = new StringBuilder();
        var blockers = new List<string>();
        // Gate B records into a spike folder outside every synced folder, so the
        // sync exclusion only has to be in place before real clips move.
        var beforeClipsMove = new List<string>();
        text.AppendLine($"medal-sync inventory {r.At:yyyy-MM-dd HH:mm} UTC on {r.Computer} ({r.User}), {r.Os}");
        text.AppendLine($"Medal {r.MedalApp ?? "?"}, recorder {r.MedalRecorder ?? "?"}, running: {(r.MedalRunning ? "yes" : "no")}");
        if (r.Compatibility is { } compatibility)
        {
            text.AppendLine("Compatibility: " + compatibility.Summary);
            blockers.AddRange(compatibility.Blockers);
        }
        if (r.Library is { } l)
        {
            text.AppendLine($"Library {l.DatabasePath}: account {l.AccountId}, schema {l.SchemaVersion} {(l.SchemaSupported ? "supported" : "UNSUPPORTED")}");
            if (!l.SchemaSupported) blockers.Add($"Medal schema {l.SchemaFingerprint} is not supported by this build.");
            text.AppendLine($"  rows: {string.Join(", ", l.Rows.Select(p => $"{p.Value} {p.Key}"))}");
            text.AppendLine($"  capture folder: {l.CaptureFolder ?? "(unset)"}, bitrate: {l.BitrateMbps?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} Mb/s");
            text.AppendLine($"  file references: {string.Join(", ", l.AssetProblems.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"))}");
            if (l.BlockingAssets > 0) blockers.Add($"{l.BlockingAssets} required library file(s) are missing or unusable.");
            if (l.UnrecognizedEmbeddedPaths > 0) text.AppendLine($"  review: {l.UnrecognizedEmbeddedPaths} unrecognized path(s) inside metadata");
        }
        else
        {
            text.AppendLine($"Library: not read ({r.LibraryError})");
            blockers.Add("The Medal library could not be read.");
        }
        if (r.CaptureTree is { } t)
        {
            text.AppendLine($"Capture tree {t.Root}: {t.Files} files, {t.Bytes / 1e9:0.00} GB; not referenced by the library: {t.UnreferencedFiles} files, {t.UnreferencedBytes / 1e9:0.00} GB");
            text.AppendLine($"  by folder: {string.Join(", ", t.TopLevelFiles.Select(p => $"{p.Key} {p.Value}"))}");
        }
        text.AppendLine($"Drives: {string.Join("; ", r.Drives.Select(d => $"{d.Letter} {d.Type} {d.Format} {d.FreeGb?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "?"} GB free"))}");
        text.AppendLine($"Proposed drive {r.ProposedDrive}: {(r.ProposedDriveFree ? "free" : "IN USE")}");
        if (!r.ProposedDriveFree) blockers.Add($"{r.ProposedDrive} is already in use on this PC.");
        if (r.Capacity is { } c)
        {
            text.AppendLine($"Cache capacity: {c.Summary}");
            if (!c.Admitted) blockers.Add("The cache SSD cannot absorb a full session.");
        }
        text.AppendLine("Startup entries (each should say via medal-sync once startup is rewired):");
        foreach (var (name, value) in r.StartupEntries) text.AppendLine($"  {name}{(value is null ? "" : $" = {value}")}");
        text.AppendLine("Syncthing folders:");
        foreach (var f in r.SyncFolders)
        {
            string note = SyncthingCheck.ActiveProfiles.Contains(f.Profile) ? "" : " [not a sign-in profile; not checked]";
            text.AppendLine($"  [{f.Profile}] {f.Id} ({f.Type}) -> {f.Path}{note}");
        }
        foreach (var e in r.SyncExclusions)
        {
            text.AppendLine($"  exclusion {e.Exclusion.FolderId} {e.Exclusion.Pattern}: {(e.Present ? "ok" : "MISSING")} - {e.Detail}");
            if (!e.Present) beforeClipsMove.Add($"Syncthing folder {e.Exclusion.FolderId} must ignore {e.Exclusion.Pattern} on every peer.");
        }
        foreach (var (name, value) in r.Dependencies)
        {
            text.AppendLine($"Dependency {name}: {value}");
            if (value.StartsWith("not", StringComparison.Ordinal)) blockers.Add($"{name} is not installed.");
        }
        foreach (var (name, value) in r.Server)
        {
            text.AppendLine($"Server {name}: {value}");
            if (value.StartsWith("not", StringComparison.Ordinal)) blockers.Add($"The server {name} path is not reachable.");
        }
        text.AppendLine(blockers.Count == 0 ? "No blockers before Gate B." : "Blockers before Gate B:");
        foreach (string blocker in blockers) text.AppendLine($"  - {blocker}");
        if (beforeClipsMove.Count > 0) text.AppendLine("Blockers before any clip moves (Phase 4):");
        foreach (string blocker in beforeClipsMove) text.AppendLine($"  - {blocker}");
        return text.ToString().TrimEnd();
    }
}

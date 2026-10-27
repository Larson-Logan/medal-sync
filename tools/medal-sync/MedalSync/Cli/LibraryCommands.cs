using System.Text.Json;
using LarsonServer.MedalSync.Library;
using LarsonServer.MedalSync.Platform;
using LarsonServer.MedalSync.Util;
using Microsoft.Data.Sqlite;

namespace LarsonServer.MedalSync.Cli;

/// <summary>Library commands. Only <c>repair</c> and <c>relocate</c> change the live database, with Medal quit and a backup first.</summary>
internal static class LibraryCommands
{
    /// <summary>The database to read: <c>--db</c>, or the signed-in Medal library.</summary>
    internal static (string Path, string AccountId) Database(Arguments args)
    {
        string? explicitPath = args.Value("--db");
        if (explicitPath is null)
        {
            var install = MedalEnvironment.Locate();
            return (install.DatabasePath, install.AccountId);
        }
        var name = Path.GetFileName(explicitPath);
        var match = System.Text.RegularExpressions.Regex.Match(name, "^medal-([0-9]+)\\.db$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) throw new UsageException("--db must name a medal-<account>.db file.");
        return (Path.GetFullPath(explicitPath), match.Groups[1].Value);
    }

    /// <summary><c>snapshot export --out FILE</c>: writes the library tables (personal data) to a new file.</summary>
    public static int Export(Arguments args, TextWriter output)
    {
        string target = Path.GetFullPath(args.Required("--out"));
        var (path, account) = Database(args);
        args.RequireAllUsed();
        var snapshot = LibraryDatabase.ReadSnapshot(path, account);
        AtomicFile.CreateNew(target, snapshot.ToCanonicalJson());
        output.WriteLine($"Wrote {target}: {Counts(snapshot)}, digest {snapshot.Digest()}.");
        output.WriteLine("It contains your library's titles and paths; keep it private and delete it when done.");
        return 0;
    }

    /// <summary><c>snapshot verify --in FILE</c>.</summary>
    public static int Verify(Arguments args, TextWriter output)
    {
        string source = args.Required("--in");
        args.RequireAllUsed();
        var snapshot = LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(source));
        bool supported = MedalSchema.SupportedFingerprints.Contains(snapshot.SchemaFingerprint);
        output.WriteLine($"{source}: account {snapshot.AccountId}, schema {snapshot.SchemaVersion} ({(supported ? "supported" : "UNSUPPORTED")}), {Counts(snapshot)}, digest {snapshot.Digest()}.");
        return supported ? 0 : 1;
    }

    /// <summary>
    /// <c>dry-run-restore --in FILE</c>: restores the snapshot into a disposable
    /// copy of this PC's library and verifies it. The live database is only read.
    /// </summary>
    public static int DryRunRestore(Arguments args, TextWriter output)
    {
        var (path, account) = Database(args);
        string? source = args.Value("--in");
        args.RequireAllUsed();
        var live = LibraryDatabase.ReadSnapshot(path, account);
        var snapshot = source is null ? live : LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(source));
        string work = Path.Combine(Path.GetTempPath(), "medal-sync-dry-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string copy = Path.Combine(work, Path.GetFileName(path));
            using (var reader = LibraryDatabase.OpenReadOnly(path))
            using (var target = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                target.Open();
                reader.BackupDatabase(target);
            }
            string devicesBefore = DeviceDigest(copy);
            // Start from a different library so the restore really rewrites every table.
            var emptied = LibrarySnapshot.Create(account, live.SchemaVersion, live.SchemaFingerprint,
                live.Tables.Select(t => t.Name == "content_counts" ? t : t with { Rows = [] }));
            LibraryRestorer.Restore(copy, emptied, new RestoreOptions(account, Path.Combine(work, "backups"), AllowEmptyReplacement: true), DateTimeOffset.UtcNow);
            var result = LibraryRestorer.Restore(copy, snapshot, new RestoreOptions(account, Path.Combine(work, "backups")), DateTimeOffset.UtcNow);
            var readBack = LibraryDatabase.ReadSnapshot(copy, account);
            bool same = readBack.Digest() == snapshot.Digest();
            bool devicesKept = DeviceDigest(copy) == devicesBefore;
            var rebuilt = CountRebuilder.Rebuild(snapshot, copy);
            bool countsConsistent = rebuilt.Digest() == snapshot.Digest();
            output.WriteLine($"Restored {Counts(snapshot)} into a disposable copy: read-back {(same ? "identical" : "DIFFERENT")}, "
                + $"device settings {(devicesKept ? "unchanged" : "CHANGED")}, rollback backup {(result.BackupPath is null ? "not needed" : "verified")}.");
            output.WriteLine(countsConsistent
                ? "Medal's stored counters match a replay through its own triggers."
                : "Medal's stored counters differ from a replay through its triggers; handoffs copy the stored counters unchanged.");
            output.WriteLine($"The live library at {path} was only read.");
            return same && devicesKept ? 0 : 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary><c>audit --checkpoint FILE</c>: what changed since a checkpoint, flagging missing-media damage.</summary>
    public static int Audit(Arguments args, TextWriter output)
    {
        var (path, account) = Database(args);
        string checkpointFile = args.Required("--checkpoint");
        bool json = args.Flag("--json");
        args.RequireAllUsed();
        var checkpoint = LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(checkpointFile));
        var audit = MissingMediaAudit.Compare(checkpoint, LibraryDatabase.ReadSnapshot(path, account));
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(audit, Json.Options));
        }
        else
        {
            foreach (var group in audit.Changes.GroupBy(c => c.Kind)) output.WriteLine($"{group.Key}: {group.Count()}");
            foreach (var change in audit.Suspect.Take(20)) output.WriteLine($"  {change.Kind} {change.ContentId} {change.Field} {change.Before}");
            output.WriteLine(audit.Suspect.Any()
                ? "Suspect changes found: entries lost paths or disappeared without a deletion record."
                : "No missing-media damage since the checkpoint.");
        }
        return audit.Suspect.Any() ? 1 : 0;
    }

    /// <summary>
    /// <c>repair --checkpoint FILE [--apply]</c>: puts back cleared paths and silently
    /// removed entries whose files exist (checked at the recorded paths, so run it
    /// while the drive is mounted). Medal must be quit; a backup is taken first.
    /// </summary>
    public static int Repair(Arguments args, TextWriter output)
    {
        var (path, account) = Database(args);
        string checkpointFile = args.Required("--checkpoint");
        bool apply = args.Flag("--apply");
        args.RequireAllUsed();
        var checkpoint = LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(checkpointFile));
        var current = LibraryDatabase.ReadSnapshot(path, account);
        var repaired = MissingMediaAudit.ProposeRepair(checkpoint, current, File.Exists, out var applied);
        foreach (var change in applied.Take(20)) output.WriteLine($"  {change.Kind} {change.ContentId} {change.Field} {change.Before}");
        output.WriteLine($"{applied.Count} repair(s) possible.");
        if (applied.Count == 0 || !apply)
        {
            if (applied.Count > 0) output.WriteLine("Preview only; quit Medal and add --apply to repair.");
            return 0;
        }
        // --db can name the live library too; the restore's own exclusive-open
        // check backs this up for any other program holding it.
        bool live = args.Value("--db") is null || IsLiveLibrary(path);
        if (live && OperatingSystem.IsWindows() && new MedalProcess(MedalEnvironment.Locate()).IsRunning())
        {
            throw new UnsafeOperationException("Quit Medal from its tray icon before repairing its library.");
        }
        var withCounts = CountRebuilder.Rebuild(repaired, path);
        // A copy named with --db keeps its backups beside it, away from this PC's real state.
        string backups = live
            ? Config.LocalState.ForCurrentUser().BackupDirectory
            : Path.Combine(Path.GetDirectoryName(path)!, "medal-sync-backups");
        var result = LibraryRestorer.Restore(path, withCounts, new RestoreOptions(account, backups), DateTimeOffset.UtcNow);
        output.WriteLine($"Repaired {applied.Count} change(s); backup {result.BackupPath}.");
        return 0;
    }

    /// <summary>
    /// <c>relocate --from DIR [--config FILE] [--apply]</c>: moves library files that
    /// Medal's storage move left outside the capture folder. Each file is copied to
    /// the same relative path in the server's clip folder and checked by SHA-256,
    /// then the library paths are rewritten through the guarded restore. The
    /// originals stay where they are. Medal must be quit.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int Relocate(Arguments args, TextWriter output)
    {
        string fromText = args.Required("--from");
        string? configPath = args.Value("--config");
        bool apply = args.Flag("--apply");
        args.RequireAllUsed();
        if (!WindowsPath.TryParse(fromText, out WindowsPath? from, out string? problem)) throw new UsageException($"--from: {problem}");
        var state = Config.LocalState.ForCurrentUser();
        var config = Config.MedalSyncConfig.Load(configPath ?? state.ConfigPath);
        var install = MedalEnvironment.Locate();
        if (install.AccountId != config.AccountId)
        {
            throw new IncompatibleLibraryException($"Medal is signed in as account {install.AccountId}, but the configuration is for {config.AccountId}.");
        }
        var snapshot = LibraryDatabase.ReadSnapshot(install.DatabasePath, install.AccountId);
        var moves = ReferenceRelocation.Plan(snapshot, from, config.Capture);
        var files = moves.GroupBy(m => m.From.Key, StringComparer.Ordinal)
            .Select(g => (Local: g.First().From.Display, Server: g.First().From.Rebase(from, config.ServerCapture).Display))
            .ToList();
        var copies = new List<(string Local, string Server)>();
        var problems = new List<string>();
        int present = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file.Local)) problems.Add($"{file.Local}: missing");
            else if (!File.Exists(file.Server)) copies.Add(file);
            else if (Hashing.Sha256File(file.Server) == Hashing.Sha256File(file.Local)) present++;
            else problems.Add($"{file.Server}: already exists with different content");
        }
        output.WriteLine($"{moves.Count} library path(s) under {from.Display} point to {files.Count} file(s): "
            + $"{copies.Count} to copy to {config.ServerCapture.Display}, {present} already there, {problems.Count} problem(s).");
        foreach (var line in problems.Take(10)) output.WriteLine("  " + line);
        if (problems.Count > 0) throw new UnsafeOperationException("Nothing was changed; resolve the problems above first.");
        if (moves.Count == 0) return 0;
        if (!apply)
        {
            output.WriteLine($"Preview only. With Medal quit, --apply copies the files and points the library at {config.Capture.Display}.");
            return 0;
        }
        if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Quit Medal from its tray icon before relocating its files.");
        foreach (var (local, server) in copies) CopyVerified(local, server);
        var relocated = ReferenceRelocation.Apply(snapshot, moves);
        var withCounts = CountRebuilder.Rebuild(relocated, install.DatabasePath);
        var result = LibraryRestorer.Restore(install.DatabasePath, withCounts, new RestoreOptions(install.AccountId, state.BackupDirectory), DateTimeOffset.UtcNow);
        output.WriteLine($"Copied {copies.Count} file(s) and repointed {moves.Count} library path(s); backup {result.BackupPath}. "
            + $"The originals under {from.Display} are unchanged.");
        return 0;
    }

    /// <summary>Copies under a temporary name, checks the copy's hash, then gives it its final name.</summary>
    private static void CopyVerified(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string partial = target + ".medal-sync-partial";
        File.Copy(source, partial, overwrite: false);
        if (Hashing.Sha256File(partial) != Hashing.Sha256File(source))
        {
            throw new IOException($"The copy of {source} at {partial} does not match; it was left there for inspection.");
        }
        File.Move(partial, target, overwrite: false);
    }

    /// <summary>Any database in Medal's own data folder is the one Medal uses.</summary>
    private static bool IsLiveLibrary(string path) =>
        string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\', '/'), Path.GetFullPath(MedalEnvironment.DataDirectory).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string DeviceDigest(string path)
    {
        using var connection = LibraryDatabase.OpenReadOnly(path);
        return LibraryDatabase.DeviceSettingsDigest(connection, null);
    }

    internal static string Counts(LibrarySnapshot snapshot) =>
        string.Join(", ", MedalSchema.LibraryTables.Select(t => $"{snapshot.RowCount(t)} {t}"));
}

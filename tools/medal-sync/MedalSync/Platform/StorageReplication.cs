using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>
/// Primary storage and required replicas pass byte verification before the coordinator releases ownership.
/// Optional replicas that are offline or failing are recorded as pending and retried on a later finish.
/// </summary>
internal sealed class StorageReplication(MedalSyncConfig config, LocalState state, Action requireIdle,
    Action<IReadOnlyList<string>>? execute = null, Action<SessionProgress>? progress = null, IAssetChecker? primary = null) : IStorageReplication
{
    private readonly IFileProbe _probe = new FileSystemProbe();
    private readonly IAssetChecker _primary = primary ?? PrimaryStorage.Checker(config, state);

    public void Prepare(LibrarySnapshot snapshot)
    {
        if (config.Replication?.LocalCopyRoot is null) return;
        requireIdle();
        var local = config.MountedCapture;
        // Claim a dedicated folder only; never adopt an unrelated tree or another library.
        if (Directory.Exists(local.Display) && Directory.EnumerateFileSystemEntries(local.Display).Any())
            RequireMarker(local);
        RequireOrdinaryDirectory(local);
        var media = ReplicationPlan.Media(snapshot, config.Capture);
        var missing = media.Where(m => !PresentLocally(m.Relative)).ToList();
        try
        {
            RequirePrimary();
        }
        catch (StorageOfflineException ex)
        {
            if (missing.Any(m => m.Required))
                throw new StorageOfflineException($"{missing.Count(m => m.Required)} clip file(s) must be downloaded from primary storage first, but it is offline. "
                    + "Open Medal again once storage is reachable; nothing was changed.", ex);
            // Every required file is already here. Record locally; primary storage is verified before handoff.
            ClaimLocal();
            Report(SessionPhase.Starting, "Primary storage is offline. Recording to this PC's local copy; clips upload when storage returns.", "Persistent local media");
            return;
        }
        // Only files this PC lacks are probed and downloaded.
        var onPrimary = missing.Where(m => _primary.ExistsOnServer(CapturePath(m.Relative))).Select(m => m.Relative).ToList();
        var nowhere = missing.Where(m => m.Required && !onPrimary.Contains(m.Relative, StringComparer.OrdinalIgnoreCase)).ToList();
        if (nowhere.Count > 0)
            throw new IOException($"Referenced media is missing on this PC and on primary storage, for example {nowhere[0].Relative}; replication was refused.");
        ClaimLocal();
        // Files only this PC holds (for example after switching primaries) are uploaded and verified at finish.
        CopyAndCheck(config.PrimaryCapture, local.Display, onPrimary, credentials: config.PrimaryIsRclone, replica: "Persistent local media");
    }

    public ReplicationResult Publish(LibrarySnapshot snapshot)
    {
        if (config.Replication is not { } replication) return ReplicationResult.None;
        requireIdle();
        RequirePrimary();
        var source = config.MountedCapture;
        RequireMarker(source);
        RequireOrdinaryDirectory(source);
        var files = ReplicationPlan.Files(snapshot, config.Capture, source, _probe);
        bool version = replication.ConflictPolicy == ReplicaConflictPolicy.Version;
        if (replication.LocalCopyRoot is not null)
        {
            try
            {
                CopyAndCheck(source.Display, config.PrimaryCapture, files, credentials: config.PrimaryIsRclone, versionConflicts: version, replica: "Primary storage");
            }
            catch (RcloneFailedException)
            {
                RequirePrimary(); // An outage becomes StorageOfflineException; anything else keeps the transfer failure.
                throw;
            }
        }
        List<string> verified = [], deferred = [];
        // Evidence is saved after every target, so a later required failure or shutdown keeps earlier deferrals.
        var pending = Pending(state, config.LibraryId).Select(p => p.Name)
            .Where(name => replication.Targets.Any(t => !t.Required && t.Name == name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.Combine(state.Root, "replication"));
        foreach (var target in replication.Targets)
        {
            requireIdle();
            try
            {
                CopyAndCheck(source.Display, ReplicationPlan.Destination(target, config.LibraryId), files, credentials: true, versionConflicts: version, replica: target.Name);
                verified.Add(target.Name);
                pending.Remove(target.Name);
            }
            catch (RcloneFailedException) when (!target.Required)
            {
                // Primary storage already holds a verified copy. An optional provider outage never holds the library.
                deferred.Add(target.Name);
                pending.Add(target.Name);
                Report(SessionPhase.Verifying, $"Cloud copy '{target.Name}' is unavailable; it will be retried on the next finish.", target.Name);
            }
            RecordPending([.. pending], DateTimeOffset.UtcNow);
        }
        requireIdle();
        RequireMarker(source);
        RequirePrimary();
        Directory.CreateDirectory(Path.Combine(state.Root, "replication"));
        AtomicFile.Replace(Path.Combine(state.Root, "replication", "last-verified.json"), Json.Serialize(new
        {
            format = 1, config.LibraryId, snapshotDigest = snapshot.Digest(), verifiedAt = DateTimeOffset.UtcNow,
            files = files.Count, localCopy = replication.LocalCopyRoot is not null, primary = config.PrimaryCapture,
            targets = verified.ToArray(), deferred = deferred.ToArray(),
        }));
        RecordPending([.. pending], DateTimeOffset.UtcNow);
        return new ReplicationResult(deferred);
    }

    /// <summary>Optional replicas waiting for a retry; empty when every copy is verified.</summary>
    internal static IReadOnlyList<PendingReplica> Pending(LocalState state, string libraryId)
    {
        string path = PendingPath(state);
        if (!File.Exists(path)) return [];
        try
        {
            var pending = Json.ReadFile<PendingReplicas>(path, "pending replicas");
            if (pending.Format != 1 || pending.LibraryId != libraryId || pending.Targets is null
                || pending.Targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.Name) || t.LastAttempt < t.Since)
                || pending.Targets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pending.Targets.Count) return [];
            return pending.Targets;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return []; }
    }

    private void RecordPending(IReadOnlyList<string> deferred, DateTimeOffset now)
    {
        var previous = Pending(state, config.LibraryId).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var targets = deferred.Select(name => new PendingReplica(name, previous.TryGetValue(name, out var old) ? old.Since : now, now)).ToArray();
        string path = PendingPath(state);
        if (targets.Length == 0) { File.Delete(path); return; }
        AtomicFile.Replace(path, Json.Serialize(new PendingReplicas(1, config.LibraryId, targets)));
    }

    private static string PendingPath(LocalState state) => Path.Combine(state.Root, "replication", "pending.json");

    private void RequirePrimary()
    {
        _primary.RequireServer();
        if (!config.PrimaryIsRclone) RequireMarker(config.ServerCapture);
    }

    private void ClaimLocal()
    {
        Directory.CreateDirectory(config.MountedCapture.Display);
        string marker = config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display;
        if (!File.Exists(marker)) AtomicFile.CreateNew(marker, System.Text.Encoding.UTF8.GetBytes(config.LibraryId + "\n"));
    }

    private bool PresentLocally(string relative)
    {
        var found = _probe.Probe(config.MountedCapture.Append(relative.Split('/')), config.MountedCapture);
        if (found.HasReparsePoint || found.IsDirectory) throw new IOException("Local media contains a link or directory in place of a clip; replication was refused.");
        return found.Exists;
    }

    private string CapturePath(string relative) => config.Capture.Append(relative.Split('/')).Display;

    private void Report(SessionPhase phase, string action, string replica)
    {
        try { progress?.Invoke(new(phase, action, Replica: replica)); }
        catch (Exception) { /* UI telemetry cannot interrupt required copies. */ }
    }

    private void CopyAndCheck(string source, string target, IReadOnlyList<string> files, bool credentials, bool versionConflicts = false, string? replica = null)
    {
        requireIdle();
        if (files.Count == 0) return;
        if (WindowsPath.TryParse(target, out var destination, out _))
        {
            RequireOrdinaryDirectory(destination);
            foreach (string file in files)
            {
                var probe = _probe.Probe(destination.Append(file.Split('/')), destination);
                if (probe.HasReparsePoint || probe.IsDirectory)
                    throw new IOException("A replica destination contains a link or directory in place of media; copying was refused.");
            }
        }
        string configFile = credentials ? Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!)
            : Path.Combine(state.Root, "rclone-empty.conf");
        if (credentials && !File.Exists(configFile)) throw new RcloneFailedException("The configured rclone credential file is missing; configure the provider before retrying finish.");
        Directory.CreateDirectory(state.Root);
        string list = Path.Combine(state.Root, "replication-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(list, files, new System.Text.UTF8Encoding(false));
            string attempt = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            string? versionDirectory = !versionConflicts ? null : WindowsPath.TryParse(target, out var localTarget, out _)
                ? localTarget.Append(".medal-sync-history", attempt).Display : target.TrimEnd('/') + "/.medal-sync-history/" + attempt;
            if (versionDirectory is not null && WindowsPath.TryParse(versionDirectory, out var localHistory, out _))
                RequireOrdinaryDirectory(localHistory);
            foreach (string command in new[] { "copy", "check" })
            {
                requireIdle();
                try { progress?.Invoke(new(command == "copy" ? SessionPhase.Uploading : SessionPhase.Verifying,
                    "Copying and verifying clips. Keep this PC on and wait for Ready.", Replica: replica)); }
                catch (Exception) { /* UI telemetry cannot interrupt required copies. */ }
                var arguments = ReplicationPlan.Arguments(command, source, target, list, configFile,
                    credentials ? config.Replication?.BandwidthLimitMiB : null, versionDirectory);
                if (execute is not null) execute(arguments); else Execute(arguments);
            }
        }
        finally { File.Delete(list); }
    }

    private void Execute(IReadOnlyList<string> arguments) =>
        new RcloneRunner(config.ExpandedRclonePath).Run(arguments, TimeSpan.FromMinutes(config.Replication?.TimeoutMinutes ?? 240), requireIdle);

    private void RequireMarker(WindowsPath folder)
    {
        RequireOrdinaryDirectory(folder);
        if (File.ReadAllText(folder.Append(MedalSyncConfig.StorageMarker).Display).Trim() != config.LibraryId)
            throw new IOException("Storage belongs to another library; replication was refused.");
    }

    private static void RequireOrdinaryDirectory(WindowsPath folder)
    {
        var current = WindowsPath.Parse(folder.Root);
        foreach (string segment in folder.Segments)
        {
            current = current.Append(segment);
            if (Directory.Exists(current.Display) && File.GetAttributes(current.Display).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Storage contains a reparse point; replication was refused.");
        }
    }
}

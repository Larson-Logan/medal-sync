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
/// <param name="volumes">Describes the volume behind a folder; replaced in tests.</param>
internal sealed class StorageReplication(MedalSyncConfig config, LocalState state, Action requireIdle,
    Action<IReadOnlyList<string>>? execute = null, Action<SessionProgress>? progress = null, IAssetChecker? primary = null,
    Func<string, VolumeInfo?>? volumes = null) : IStorageReplication
{
    private readonly IFileProbe _probe = new FileSystemProbe();
    private readonly IAssetChecker _primary = primary ?? PrimaryStorage.Checker(config, state);
    private readonly Func<string, VolumeInfo?> _volumes = volumes ?? StorageVolume.Describe;
    private int _readBackFiles;
    private long _readBackBytes;
    private int _verifiedDestinations;

    private ReplicationResult Result(IReadOnlyList<string> deferred) => new(deferred)
    { ReadBackFiles = _readBackFiles, ReadBackBytes = _readBackBytes, VerifiedDestinations = _verifiedDestinations };

    /// <summary>Copies only pending optional targets of a committed snapshot. The caller guards Ready and its exact generation.</summary>
    public ReplicationResult RetryPending(LibrarySnapshot snapshot, string? targetName = null)
    {
        _readBackFiles = 0; _readBackBytes = 0; _verifiedDestinations = 0;
        var replication = config.Replication ?? throw new UnsafeOperationException("No cloud copies are configured.");
        var pending = Pending(state, config.LibraryId).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = replication.Targets.Where(t => !t.Required && pending.Contains(t.Name)
            && (targetName is null || t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (targetName is not null && targets.Length == 0)
            throw new UnsafeOperationException("The named target is not a pending optional cloud copy.");
        if (targets.Length == 0) return ReplicationResult.None;
        requireIdle();
        var source = config.PersistentMediaRoot is null ? config.ServerCapture : config.MountedCapture;
        RequireMarker(source);
        var files = ReplicationPlan.Files(snapshot, config.Capture, source, _probe);
        List<string> deferred = [];
        foreach (var target in targets)
        {
            string destination = ReplicationPlan.Destination(target, config.LibraryId);
            bool complete = false;
            try
            {
                requireIdle();
                VerifyDestination(snapshot, source.Display, destination, files, true,
                    replication.ConflictPolicy == ReplicaConflictPolicy.Version, target.Name);
                requireIdle();
                RequireMarker(source);
                pending.Remove(target.Name);
                RecordPending([.. pending], DateTimeOffset.UtcNow, target.Name);
                complete = true;
            }
            catch (RcloneFailedException)
            {
                requireIdle();
                deferred.Add(target.Name);
                RecordPending([.. pending], DateTimeOffset.UtcNow, target.Name);
            }
            finally
            {
                if (!complete)
                {
                    pending.Add(target.Name);
                    new ReplicationVerification(config, state, source.Display, destination, snapshot.Digest(), files, DateTimeOffset.UtcNow).Invalidate();
                    // Cancellation keeps the pending target and records that this attempt did not finish.
                    RecordPending([.. pending], DateTimeOffset.UtcNow, target.Name);
                }
            }
        }
        requireIdle();
        return Result(deferred);
    }

    public string? Prepare(LibrarySnapshot snapshot)
    {
        if (config.Replication?.LocalCopyRoot is not { } localRoot) return null;
        requireIdle();
        // An unplugged disk is an outage to wait for, not a folder to create; an unsuitable one is refused before any download.
        StorageVolume.RequireSuitable(StorageRole.Media, localRoot, StorageVolume.RequirePresent(localRoot, "persistent clip storage", _volumes));
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
            return null;
        }
        catch (StorageActionRequiredException ex)
        {
            if (missing.Any(m => m.Required))
                throw new StorageActionRequiredException($"{missing.Count(m => m.Required)} clip file(s) must be downloaded from primary storage first. {ex.Message} "
                    + "Nothing was changed.", ex);
            // Recording can go ahead locally, but the handoff cannot finish until this is fixed. Say so now, not after the session.
            ClaimLocal();
            string warning = $"Medal started, but the switch to another PC cannot finish until this is fixed: {ex.Message}";
            Report(SessionPhase.Starting, warning, "Persistent local media");
            return warning;
        }
        // Only files this PC lacks are probed and downloaded.
        var onPrimary = missing.Where(m => _primary.ExistsOnServer(CapturePath(m.Relative))).Select(m => m.Relative).ToList();
        var nowhere = missing.Where(m => m.Required && !onPrimary.Contains(m.Relative, StringComparer.OrdinalIgnoreCase)).ToList();
        if (nowhere.Count > 0)
            throw new IOException($"Referenced media is missing on this PC and on primary storage, for example {nowhere[0].Relative}; replication was refused.");
        RequireDownloadSpace(localRoot, onPrimary);
        ClaimLocal();
        // Files only this PC holds (for example after switching primaries) are uploaded and verified at finish.
        CopyAndCheck(config.PrimaryCapture, local.Display, onPrimary, credentials: config.PrimaryIsRclone, replica: "Persistent local media");
        return null;
    }

    public ReplicationResult Publish(LibrarySnapshot snapshot)
    {
        _readBackFiles = 0; _readBackBytes = 0; _verifiedDestinations = 0;
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
                VerifyDestination(snapshot, source.Display, config.PrimaryCapture, files, config.PrimaryIsRclone, version, "Primary storage");
            }
            catch (RcloneFailedException ex)
            {
                RequirePrimary(); // An outage or a sign-in problem surfaces here with its own message.
                throw Required(ex, $"Primary storage {config.PrimaryCapture}", config.PrimaryCapture, config.PrimaryIsRclone);
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
                VerifyDestination(snapshot, source.Display, ReplicationPlan.Destination(target, config.LibraryId), files, true, version, target.Name);
                verified.Add(target.Name);
                pending.Remove(target.Name);
            }
            catch (RcloneFailedException ex) when (target.Required)
            {
                string destination = ReplicationPlan.Destination(target, config.LibraryId);
                throw Required(ex, $"Required cloud copy '{target.Name}' ({destination})", destination, credentials: true);
            }
            catch (RcloneFailedException)
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
        return Result(deferred);
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

    /// <summary>Queues one optional target for <c>replicate retry</c>, keeping any existing entry as it is.</summary>
    internal static void MarkPending(LocalState state, string libraryId, string name, DateTimeOffset now)
    {
        var pending = Pending(state, libraryId).ToList();
        if (pending.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        pending.Add(new PendingReplica(name, now, now));
        Directory.CreateDirectory(Path.GetDirectoryName(PendingPath(state))!);
        AtomicFile.Replace(PendingPath(state), Json.Serialize(new PendingReplicas(1, libraryId, pending.ToArray())));
    }

    private void RecordPending(IReadOnlyList<string> deferred, DateTimeOffset now, string? attempted = null)
    {
        var previous = Pending(state, config.LibraryId).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var targets = deferred.Select(name => new PendingReplica(name, previous.TryGetValue(name, out var old) ? old.Since : now,
            attempted is null || name.Equals(attempted, StringComparison.OrdinalIgnoreCase) || old is null ? now : old.LastAttempt)).ToArray();
        string path = PendingPath(state);
        if (targets.Length == 0) { File.Delete(path); return; }
        AtomicFile.Replace(path, Json.Serialize(new PendingReplicas(1, config.LibraryId, targets)));
    }

    /// <summary>
    /// A failure at a destination that must verify before handoff. Waiting may fix an outage, so it becomes a calm,
    /// backed-off wait; anything else (sign-in, conflict, quota, failed verification) names its cause and next step.
    /// </summary>
    private IOException Required(RcloneFailedException ex, string where, string destination, bool credentials) =>
        RcloneFailure.IsTransient(ex)
            ? new StorageOfflineException($"{where} cannot be reached right now.", ex)
            : new StorageActionRequiredException(RcloneFailure.Describe(ex, where, destination,
                credentials ? Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!) : null), ex);

    private static string PendingPath(LocalState state) => Path.Combine(state.Root, "replication", "pending.json");

    private void RequirePrimary()
    {
        _primary.RequireServer();
        if (!config.PrimaryIsRclone) RequireMarker(config.ServerCapture);
    }

    /// <summary>Refuses a download that would leave less than the configured free-space reserve, before any byte is copied.</summary>
    private void RequireDownloadSpace(string localRoot, IReadOnlyList<string> downloads)
    {
        if (downloads.Count == 0) return;
        long bytes = 0;
        foreach (string relative in downloads)
        {
            long size = Math.Max(0, _primary.SizeOnServer(CapturePath(relative)) ?? 0);
            bytes = bytes > long.MaxValue - size ? long.MaxValue : bytes + size;
        }
        if (bytes == 0) return; // Nothing measurable to budget.
        var volume = StorageVolume.RequirePresent(config.MountedCapture.Display, "persistent clip storage", _volumes);
        long reserve = (long)config.Mount.MinFreeGiB << 30;
        if (bytes > volume.FreeBytes - reserve)
            throw new UnsafeOperationException($"{downloads.Count} clip file(s) ({StorageVolume.Gb(bytes)}) must be downloaded to {localRoot}, "
                + $"but {volume.MountPath} has {StorageVolume.Gb(volume.FreeBytes)} free and keeps a {StorageVolume.Gb(reserve)} reserve. "
                + "Free space or choose a larger disk for localCopyRoot; nothing was downloaded.");
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

    private void VerifyDestination(LibrarySnapshot snapshot, string source, string target, IReadOnlyList<string> files,
        bool credentials, bool versionConflicts, string replica)
    {
        var verification = new ReplicationVerification(config, state, source, target, snapshot.Digest(), files, DateTimeOffset.UtcNow);
        string detail = "";
        verification.Run((delta, _) => CopyAndCheck(source, target, delta, credentials, versionConflicts, replica, detail: detail),
            unchanged => CopyAndCheck(source, target, unchanged, credentials, replica: replica, sizeOnly: true, detail: detail),
            (count, bytes, full) =>
            {
                _readBackFiles += count; _readBackBytes += bytes;
                detail = $"{(full ? "Full" : "Incremental")} verification: reading back {count} file(s), {bytes} bytes; checking remaining file sizes.";
                Report(SessionPhase.Verifying, detail, replica);
            }, requireIdle);
        _verifiedDestinations++;
    }

    private void CopyAndCheck(string source, string target, IReadOnlyList<string> files, bool credentials, bool versionConflicts = false, string? replica = null, bool sizeOnly = false, string? detail = null)
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
        if (credentials && !File.Exists(configFile))
            throw new RcloneFailedException($"The rclone credential file is missing at {configFile}. Copy it to this PC from the PC where you ran 'rclone config', or create it there again.");
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
            foreach (string command in sizeOnly ? new[] { "check" } : new[] { "copy", "check" })
            {
                requireIdle();
                try { progress?.Invoke(new(command == "copy" ? SessionPhase.Uploading : SessionPhase.Verifying,
                    detail is null ? "Copying and verifying clips. Keep this PC on and wait for Ready."
                        : (command == "copy" ? "Copying selected clips. " : "Checking selected clips. ") + detail, Replica: replica)); }
                catch (Exception) { /* UI telemetry cannot interrupt required copies. */ }
                var arguments = ReplicationPlan.Arguments(command, source, target, list, configFile,
                    credentials ? config.Replication?.BandwidthLimitMiB : null, versionDirectory, sizeOnly);
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

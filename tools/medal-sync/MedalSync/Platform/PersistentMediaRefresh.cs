using System.Security.Cryptography;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

internal sealed record MediaIdentity(long Bytes, long ModifiedUtcTicks, string Sha256);
internal sealed record MediaRefreshFile(string Path, MediaIdentity Server, MediaIdentity? Local, string? PriorServerVersion)
{
    public bool Conflict => Local is not null && Local.Sha256 != Server.Sha256;
    public string Classification => !Conflict ? "matching or missing" : PriorServerVersion is null
        ? "unknown local modification; explicit approval required" : "matches preserved server history";
}
internal sealed record MediaRefreshPlan(int Format, string Id, string LibraryId, string DeviceId, string ConfigDigest,
    long Generation, long Revision, string SnapshotDigest, IReadOnlyList<MediaRefreshFile> Files);

/// <summary>An explicit review of exact bytes. Ordinary hydration remains immutable.</summary>
internal sealed class PersistentMediaRefresh(MedalSyncConfig config, LocalState state, DeviceIdentity device,
    CoordinationStore store, Action requireIdle, Action requireCompatible, Action<string>? afterReplacement = null)
{
    internal const string ReasonPrefix = "persistent-media-refresh:";
    public static bool IsPending(OwnershipRecord journal) => journal.Reason?.StartsWith(ReasonPrefix, StringComparison.Ordinal) == true;
    private readonly FileSystemProbe _probe = new();

    public MediaRefreshPlan Preview(string? id = null)
    {
        RequireEnvironment();
        var journal = store.ReadJournal();
        if (journal.State != OwnershipState.Clean) throw new UnsafeOperationException("Finish or recover the owned session before reviewing persistent media.");
        var generation = store.Generations.Read(journal.CommittedGeneration);
        var paths = ReplicationPlan.Files(generation.Snapshot, config.Capture, config.ServerCapture, _probe);
        var files = paths.Select(path =>
        {
            var server = Identity(config.ServerCapture, path)!;
            var local = Identity(config.MountedCapture, path, optional: true);
            string? prior = local is not null && local.Sha256 != server.Sha256 ? FindPrior(path, local.Sha256) : null;
            return new MediaRefreshFile(path, server, local, prior);
        }).ToArray();
        if (store.ReadJournal() != journal) throw new UnsafeOperationException("Ownership changed during review; create a fresh plan.");
        return new(1, id ?? SessionCoordinator.NewId(), config.LibraryId, device.DeviceId, ConfigDigest(),
            journal.CommittedGeneration, journal.Revision, generation.Snapshot.Digest(), files);
    }

    public string Apply(MediaRefreshPlan plan, bool acceptUnknown)
    {
        RequireEnvironment();
        ValidatePlan(plan);
        if (plan.LibraryId != config.LibraryId
            || plan.DeviceId != device.DeviceId || plan.ConfigDigest != ConfigDigest() || plan.Files is null)
            throw new UnsafeOperationException("The media review belongs to another configuration/device or is invalid; create a fresh plan.");
        if (!acceptUnknown && plan.Files.Any(f => f.Conflict && f.PriorServerVersion is null))
            throw new UnsafeOperationException("Unknown local modifications are preserved. Review every path and use --accept-unknown-media only to approve these exact bytes.");
        string digest = Hashing.Sha256Hex(Json.Serialize(plan));
        string reason = ReasonPrefix + digest;
        string recovery = WindowsPath.Parse(state.BackupDirectory).Append("media-refresh-" + plan.Id).Display;
        var journal = store.ReadJournal();
        if (journal.State == OwnershipState.Clean)
        {
            var fresh = Preview(plan.Id);
            if (Hashing.Sha256Hex(Json.Serialize(fresh)) != digest)
                throw new UnsafeOperationException("Reviewed media, generation or ownership changed; create a fresh plan. Nothing was replaced.");
            // A server lock covers the last review and ownership acquisition; another PC cannot slip between them.
            store.Update(current =>
            {
                if (current != journal) throw new OwnershipConflictException("Ownership changed after review.");
                return Ownership.Acquire(current, device, plan.Id, DateTimeOffset.UtcNow) with { Reason = reason };
            }, TimeSpan.FromSeconds(10));
        }
        else if (journal.State != OwnershipState.Starting || !journal.IsOwnedBy(device, plan.Id) || journal.Reason != reason)
            throw new UnsafeOperationException("Another session owns this library. Recover it before refreshing media.");

        // Write evidence before touching media. Any subsequent failure keeps STARTING ownership and all backups.
        RequireOrdinary(WindowsPath.Parse(recovery));
        Directory.CreateDirectory(recovery);
        string evidence = Path.Combine(recovery, "review.json");
        if (File.Exists(evidence))
        {
            if (Hashing.Sha256Hex(File.ReadAllBytes(evidence)) != digest) throw new InvalidDataException("The saved media review changed; recovery was refused.");
        }
        else AtomicFile.CreateNew(evidence, Json.Serialize(plan));

        RequireGeneration(plan, verifyFileSet: true);

        foreach (var file in plan.Files)
        {
            // Serialize each replacement against journal transitions, including an explicit abandon.
            using var writer = store.AcquireWriterLock(TimeSpan.FromSeconds(10));
            RequireOwned(plan, reason);
            RequireEnvironment();
            RequireGeneration(plan);
            if (Identity(config.ServerCapture, file.Path) != file.Server)
                throw new UnsafeOperationException("Server media changed after review; ownership and recovery files are retained.");
            var local = Identity(config.MountedCapture, file.Path, optional: true);
            string backup = WindowsPath.Parse(recovery).Append(["previous", ..Segments(file.Path)]).Display;
            if (file.Conflict)
            {
                if (file.PriorServerVersion is { } prior
                    && Identity(config.ServerCapture.Append(".medal-sync-history"), prior)?.Sha256 != file.Local!.Sha256)
                    throw new UnsafeOperationException("The retained server history changed after review; refresh was refused.");
                var preserved = Identity(WindowsPath.Parse(recovery).Append("previous"), file.Path, optional: true);
                if (preserved is null)
                {
                    if (local != file.Local) throw new UnsafeOperationException("Local media changed after review; replacement was refused.");
                    RequireOrdinary(WindowsPath.Parse(backup));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(config.MountedCapture.Append(Segments(file.Path)).Display, backup, overwrite: false);
                    if (Identity(WindowsPath.Parse(recovery).Append("previous"), file.Path)?.Sha256 != file.Local!.Sha256)
                        throw new IOException("The previous media backup did not verify; ownership is retained.");
                }
                else if (preserved.Sha256 != file.Local!.Sha256)
                    throw new IOException("The retained media backup changed; ownership is retained.");
            }
            bool alreadyReplaced = local?.Sha256 == file.Server.Sha256;
            if (!alreadyReplaced && local != file.Local)
                throw new UnsafeOperationException("Local media changed since review; ownership and prior bytes are retained.");
            if (!alreadyReplaced)
            {
                string target = config.MountedCapture.Append(Segments(file.Path)).Display;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Stage on the destination volume, verify, then atomically rename over the reviewed file.
                string staging = target + ".medal-sync-refresh-" + plan.Id;
                RequireOrdinary(WindowsPath.Parse(staging));
                if (File.Exists(staging) && Identity(config.MountedCapture, file.Path + ".medal-sync-refresh-" + plan.Id)!.Sha256 != file.Server.Sha256)
                {
                    // Preserve a partial write instead of deleting possible recovery evidence.
                    string partial = Path.Combine(recovery, "partial-" + Guid.NewGuid().ToString("N"));
                    File.Move(staging, partial, overwrite: false);
                }
                if (!File.Exists(staging)) File.Copy(config.ServerCapture.Append(Segments(file.Path)).Display, staging, overwrite: false);
                if (Identity(config.MountedCapture, file.Path + ".medal-sync-refresh-" + plan.Id)!.Sha256 != file.Server.Sha256)
                    throw new IOException("The staged replacement did not verify; ownership and recovery evidence are retained.");
                RequireEnvironment();
                RequireOwned(plan, reason);
                if (Identity(config.ServerCapture, file.Path) != file.Server || Identity(config.MountedCapture, file.Path, optional: true) != local)
                    throw new UnsafeOperationException("Media changed during copying; the staged bytes and ownership are retained.");
                File.Move(staging, target, overwrite: true);
                afterReplacement?.Invoke(file.Path);
            }
            if (Identity(config.MountedCapture, file.Path)!.Sha256 != file.Server.Sha256)
                throw new IOException("Replacement media did not verify; ownership is retained.");
        }
        // Check the whole reviewed set again, so a later copy cannot hide an earlier change.
        store.Update(current =>
        {
            RequireOwned(plan, reason);
            RequireEnvironment();
            RequireGeneration(plan, verifyFileSet: true);
            foreach (var file in plan.Files)
                if (Identity(config.ServerCapture, file.Path) != file.Server
                    || Identity(config.MountedCapture, file.Path)!.Sha256 != file.Server.Sha256)
                    throw new IOException("Final media verification failed; ownership and backups are retained.");
            return Ownership.AbortStart(current, device, plan.Id, "reviewed persistent media verified; recovery bytes retained", DateTimeOffset.UtcNow);
        }, TimeSpan.FromSeconds(10));
        return recovery;
    }

    private void RequireOwned(MediaRefreshPlan plan, string reason)
    {
        var journal = store.ReadJournal();
        if (journal.State != OwnershipState.Starting || !journal.IsOwnedBy(device, plan.Id) || journal.Reason != reason)
            throw new UnsafeOperationException("Refresh ownership changed; no further files will be replaced.");
    }

    private void RequireGeneration(MediaRefreshPlan plan, bool verifyFileSet = false)
    {
        var journal = store.ReadJournal();
        var manifest = store.Generations.ReadManifest(journal.CommittedGeneration);
        if (journal.CommittedGeneration != plan.Generation || manifest.SnapshotDigest != plan.SnapshotDigest)
            throw new UnsafeOperationException("The committed library changed; recovery evidence and ownership are retained.");
        if (!verifyFileSet) return;
        var generation = store.Generations.Read(journal.CommittedGeneration);
        var paths = ReplicationPlan.Files(generation.Snapshot, config.Capture, config.ServerCapture, _probe);
        if (!paths.SequenceEqual(plan.Files.Select(f => f.Path), StringComparer.Ordinal))
            throw new UnsafeOperationException("The reviewed file set differs from the committed library.");
    }

    private string? FindPrior(string path, string sha256)
    {
        var history = config.ServerCapture.Append(".medal-sync-history");
        RequireOrdinary(history);
        if (!Directory.Exists(history.Display)) return null;
        var attempts = Directory.EnumerateDirectories(history.Display).Take(1001).ToArray();
        if (attempts.Length > 1000) return null; // Unknown is conservative; do not guess from an incomplete search.
        foreach (string attempt in attempts.Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(attempt);
            var prior = Identity(history.Append(name), path, optional: true);
            if (prior?.Sha256 == sha256) return name + "/" + path;
        }
        return null;
    }

    private string ConfigDigest() => Hashing.Sha256Hex(Json.Serialize(config));

    private static void ValidatePlan(MediaRefreshPlan plan)
    {
        static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        static bool ValidIdentity(MediaIdentity? identity) => identity is not null && identity.Bytes >= 0
            && identity.ModifiedUtcTicks >= DateTime.MinValue.Ticks && identity.ModifiedUtcTicks <= DateTime.MaxValue.Ticks && Digest(identity.Sha256);
        if (plan.Format != 1 || string.IsNullOrEmpty(plan.Id) || !CoordinationStore.IsValidId(plan.Id)
            || !Digest(plan.ConfigDigest) || !Digest(plan.SnapshotDigest) || plan.Generation < 0 || plan.Revision < 1 || plan.Files is null
            || plan.Files.Any(f => f is null || !ValidIdentity(f.Server) || (f.Local is not null && !ValidIdentity(f.Local))))
            throw new InvalidDataException("The media review is incomplete or malformed; create a fresh plan.");
        foreach (var file in plan.Files)
        {
            foreach (string path in file.PriorServerVersion is null ? new[] { file.Path } : new[] { file.Path, file.PriorServerVersion })
            {
                string[] parts = Segments(path);
                if (!WindowsPath.TryParse(@"C:\review\" + string.Join('\\', parts), out _, out _))
                    throw new InvalidDataException("The media review contains an unsafe relative path.");
            }
        }
        if (plan.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Files.Count)
            throw new InvalidDataException("The media review contains duplicate paths.");
    }

    private void RequireEnvironment()
    {
        if (!WindowsPath.TryParse(config.Remote, out _, out _))
            throw new UnsafeOperationException("Reviewed persistent-media refresh requires a filesystem primary; this storage backend is not supported by refresh-media.");
        if (config.Replication?.LocalCopyRoot is null) throw new UnsafeOperationException("Persistent local media is not configured.");
        requireIdle();
        requireCompatible();
        foreach (var root in new[] { config.ServerCapture, config.MountedCapture })
        {
            RequireOrdinary(root.Append(MedalSyncConfig.StorageMarker));
            if (File.ReadAllText(root.Append(MedalSyncConfig.StorageMarker).Display).Trim() != config.LibraryId)
                throw new UnsafeOperationException("Storage belongs to another library; refresh was refused.");
        }
    }

    private MediaIdentity? Identity(WindowsPath root, string relative, bool optional = false)
    {
        var path = root.Append(Segments(relative));
        RequireOrdinary(path);
        if (!File.Exists(path.Display) && optional) return null;
        using var stream = new FileStream(path.Display, FileMode.Open, FileAccess.Read, FileShare.Read);
        long modified = File.GetLastWriteTimeUtc(path.Display).Ticks;
        return new(stream.Length, modified, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    private static string[] Segments(string relative)
    {
        if (string.IsNullOrEmpty(relative)) throw new InvalidDataException("The media review has an empty relative path.");
        var parts = relative.Split('/');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains('\\') || p.Contains(':')))
            throw new InvalidDataException("Invalid relative media path.");
        return parts;
    }

    private static void RequireOrdinary(WindowsPath path)
    {
        var current = WindowsPath.Parse(path.Root);
        foreach (string segment in path.Segments)
        {
            current = current.Append(segment);
            if ((Directory.Exists(current.Display) || File.Exists(current.Display))
                && File.GetAttributes(current.Display).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Refresh refuses reparse points in media, history or recovery paths.");
        }
    }
}

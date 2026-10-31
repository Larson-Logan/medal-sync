using System.Globalization;
using System.Text;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

internal sealed record ReplicaPruneFile(string Path, long Size, DateTimeOffset ModTime, string Category);

/// <summary>
/// A reviewed set of files to move to a destination's trash. It is bound to the library, committed generation, target,
/// destination and a digest of every file listed there outside the trash, so any change between review and apply is refused.
/// </summary>
internal sealed record ReplicaPrunePlan(int Format, string Id, string LibraryId, string Target, string Destination, long Generation,
    string SnapshotDigest, DateTimeOffset CreatedAt, int GraceDays, int? HistoryOlderThanDays, int Listed, string ListingDigest,
    int LeftAlone, IReadOnlyList<ReplicaPruneFile> Files, string Integrity = "")
{
    public const int CurrentFormat = 1;
    public string TrashFolder => ReplicaPrune.TrashRoot + "/" + Id;
}

internal sealed record ReplicaPruneResult(int Moved, long Bytes, int AlreadyMoved, string TrashFolder);

internal sealed record ReplicaTrashBatch(string Name, DateTimeOffset? CreatedAt, int Files, long Bytes);

/// <summary>
/// Removes cloud replica files the committed generation no longer references, in two explicit steps: a reviewed apply
/// moves exactly the planned files into <c>.medal-sync-trash/</c> at the destination, and a separate purge deletes
/// old trash. Referenced files, recent files (by modification time) and files referenced by a recent uncommitted
/// generation are never planned. Version history is a separate opt-in category. The library database is never touched.
/// </summary>
internal sealed class ReplicaPrune(MedalSyncConfig config, LocalState state, CoordinationStore store, Action requireIdle,
    IReplicaRemote remote, Func<DateTimeOffset>? clock = null)
{
    public const string TrashRoot = ".medal-sync-trash";
    public const string HistoryRoot = ".medal-sync-history";
    /// <summary>An empty file in a trash batch named after when files were moved into it: <c>.medal-sync-moved-yyyyMMddTHHmmssfffZ</c>.</summary>
    public const string MovedMarker = ".medal-sync-moved-";
    public const string Unreferenced = "unreferenced";
    public const string History = "history";
    public const int DefaultGraceDays = 14;
    public const int DefaultPurgeDays = 7;
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".heic", ".json",
    };

    public ReplicaPrunePlan Plan(string targetName, int graceDays = DefaultGraceDays, int? historyOlderThanDays = null)
    {
        if (graceDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(graceDays));
        if (historyOlderThanDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(historyOlderThanDays));
        requireIdle();
        var target = Target(targetName);
        var journal = RequireReady();
        var generation = store.Generations.Read(journal.CommittedGeneration);
        string destination = ReplicationPlan.Destination(target, config.LibraryId);
        var now = Now;
        var protectedPaths = Protected(generation.Snapshot, journal.CommittedGeneration, now - TimeSpan.FromDays(graceDays));
        var listing = Live(remote.List(destination));
        requireIdle();
        var files = new List<ReplicaPruneFile>();
        int leftAlone = 0;
        foreach (var file in listing)
        {
            string[] parts = file.Path.Split('/');
            if (parts[0].Equals(HistoryRoot, StringComparison.OrdinalIgnoreCase))
            {
                // .medal-sync-history/<UTC attempt time>-<id>/<path>: only whole, dated, old enough attempts.
                if (historyOlderThanDays is { } days && parts.Length > 2 && AttemptTime(parts[1]) is { } stamp && now - stamp >= TimeSpan.FromDays(days))
                    files.Add(new(file.Path, file.Size, file.ModTime, History));
                else leftAlone++;
                continue;
            }
            bool ours = MediaExtensions.Contains(System.IO.Path.GetExtension(file.Path)) && !file.Path.Equals(MedalSyncConfig.StorageMarker, StringComparison.OrdinalIgnoreCase)
                && !parts.Any(p => p.Equals("Databases", StringComparison.OrdinalIgnoreCase));
            if (!ours) { leftAlone++; continue; }
            if (protectedPaths.Contains(file.Path) || file.ModTime > now - TimeSpan.FromDays(graceDays)) continue;
            files.Add(new(file.Path, file.Size, file.ModTime, Unreferenced));
        }
        var plan = new ReplicaPrunePlan(ReplicaPrunePlan.CurrentFormat,
            now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + ShortId(),
            config.LibraryId, target.Name, destination, journal.CommittedGeneration, generation.Snapshot.Digest(), now, graceDays,
            historyOlderThanDays, listing.Count, Digest(listing), leftAlone, files);
        if (store.ReadJournal() != journal) throw new UnsafeOperationException("The library changed during the review; preview again.");
        return plan with { Integrity = IntegrityOf(plan) };
    }

    /// <summary>
    /// Moves the planned files that are still in place, after proving that nothing else at the destination changed.
    /// Repeating an interrupted apply with the same plan finishes it. The caller holds the journal writer lock.
    /// </summary>
    public ReplicaPruneResult Apply(ReplicaPrunePlan plan)
    {
        Validate(plan);
        requireIdle();
        var target = Target(plan.Target);
        var journal = RequireReady();
        string destination = ReplicationPlan.Destination(target, config.LibraryId);
        if (plan.LibraryId != config.LibraryId || plan.Destination != destination)
            throw new UnsafeOperationException("The cleanup plan belongs to another library or destination; preview again.");
        var generation = store.Generations.Read(journal.CommittedGeneration);
        if (journal.CommittedGeneration != plan.Generation || generation.Snapshot.Digest() != plan.SnapshotDigest)
            throw new UnsafeOperationException("The committed generation changed since the review; preview the cleanup again. Nothing was moved.");
        // Recomputed now, never trusted from the file: a referenced or recently used file is never moved.
        var protectedPaths = Protected(generation.Snapshot, journal.CommittedGeneration, plan.CreatedAt - TimeSpan.FromDays(plan.GraceDays));
        if (plan.Files.FirstOrDefault(f => f.Category == Unreferenced && protectedPaths.Contains(f.Path)) is { } kept)
            throw new UnsafeOperationException($"{kept.Path} is referenced by the library again; preview the cleanup again. Nothing was moved.");

        var (remaining, alreadyMoved) = Remaining(plan, remote.List(destination));
        requireIdle();
        if (remaining.Count > 0)
        {
            // Trash age counts from the move, not the review: a late or resumed apply still gets the full wait before purge.
            // One marker before moving covers an interrupted move; one after covers a long one.
            remote.Mark(destination, plan.TrashFolder, Marker(Now));
            remote.Move(destination, remaining.Select(f => f.Path).ToArray(), plan.TrashFolder);
            requireIdle();
            remote.Mark(destination, plan.TrashFolder, Marker(Now));
        }
        requireIdle();
        var (left, moved) = Remaining(plan, remote.List(destination));
        if (left.Count > 0) throw new IOException($"{left.Count} reviewed file(s) are still in place after the move; apply the same plan again.");
        Directory.CreateDirectory(Path.Combine(state.Root, "replication", "prune"));
        AtomicFile.Replace(Path.Combine(state.Root, "replication", "prune", plan.Id + ".json"), Json.Serialize(new
        {
            format = 1, plan.Id, plan.Target, plan.Generation, files = plan.Files.Count, bytes = plan.Files.Sum(f => f.Size),
            trash = plan.TrashFolder, completedAt = Now,
        }));
        return new(remaining.Count, plan.Files.Sum(f => f.Size), alreadyMoved, plan.TrashFolder);
    }

    /// <summary>Trash batches at a destination. A batch's age is its newest move marker; a batch without one has no age.</summary>
    public IReadOnlyList<ReplicaTrashBatch> Trash(string targetName)
    {
        requireIdle();
        var target = Target(targetName);
        var files = remote.List(ReplicationPlan.Destination(target, config.LibraryId).TrimEnd('/') + "/" + TrashRoot);
        return files.Where(f => f.Path.Split('/').Length > 1)
            .GroupBy(f => f.Path.Split('/')[0], StringComparer.Ordinal)
            .Select(g =>
            {
                var markers = g.Where(f => f.Path.Split('/') is { Length: 2 } parts && parts[1].StartsWith(MovedMarker, StringComparison.Ordinal)).ToArray();
                var moved = markers.Select(f => MarkerTime(f.Path.Split('/')[1])).Where(t => t is not null).Max();
                var media = g.Except(markers).ToArray();
                return new ReplicaTrashBatch(g.Key, moved, media.Length, media.Sum(f => f.Size));
            })
            .OrderBy(b => b.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Deletes trash batches whose files were moved at least <paramref name="olderThanDays"/> ago. Batches without a move marker are kept.</summary>
    public IReadOnlyList<ReplicaTrashBatch> Purge(string targetName, int olderThanDays, bool apply,
        Action<IReadOnlyList<ReplicaTrashBatch>, IReadOnlyList<ReplicaTrashBatch>>? report = null)
    {
        if (olderThanDays is < 0 or > 3650) throw new ArgumentOutOfRangeException(nameof(olderThanDays));
        RequireReady();
        var target = Target(targetName);
        var now = Now;
        var all = Trash(targetName);
        var due = all.Where(b => b.CreatedAt is { } at && now - at >= TimeSpan.FromDays(olderThanDays)).ToArray();
        // Report and deletion use this one snapshot. A batch that becomes old enough later stays kept.
        report?.Invoke(all, due);
        if (!apply) return due;
        foreach (var batch in due)
        {
            requireIdle();
            remote.Purge(ReplicationPlan.Destination(target, config.LibraryId), TrashRoot + "/" + batch.Name);
        }
        return due;
    }

    private ReplicaTarget Target(string name)
    {
        var target = config.Replication?.Targets.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new UnsafeOperationException("--target must name a configured cloud copy.");
        if (target.Required) throw new UnsafeOperationException(
            $"'{target.Name}' is a required copy. Cleanup currently supports optional cloud copies only; nothing was changed.");
        return target;
    }

    private OwnershipRecord RequireReady()
    {
        var journal = store.ReadJournal();
        if (journal.State != OwnershipState.Clean)
            throw new UnsafeOperationException("Cleaning up a cloud copy requires a Ready library. Finish or recover the current session first.");
        return journal;
    }

    /// <summary>
    /// Referenced by the committed generation, or by an uncommitted generation published after <paramref name="since"/>.
    /// Uncommitted means outside the committed ancestry: an abandoned attempt can sit below the head once a later publish succeeds.
    /// </summary>
    private HashSet<string> Protected(Library.LibrarySnapshot committed, long head, DateTimeOffset since)
    {
        var paths = ReplicationPlan.Media(committed, config.Capture).Select(m => m.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<long> ancestry;
        try { ancestry = store.Generations.Chain(head).Select(c => c.Generation).ToHashSet(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new UnsafeOperationException($"The committed history cannot be followed ({ex.Message}), so unfinished switches cannot be ruled out; nothing was planned. "
                + "Run 'medal-sync history list' to see where it breaks.");
        }
        foreach (long number in store.Generations.Numbered().Where(n => !ancestry.Contains(n)))
        {
            try
            {
                var later = store.Generations.Read(number);
                if (later.Manifest.CreatedAt < since) continue;
                foreach (var media in ReplicationPlan.Media(later.Snapshot, config.Capture)) paths.Add(media.Relative);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or MedalSyncException)
            {
                // An unreadable attempt that may be recent cannot be proven unrelated, so nothing is planned while it is there.
                string folder = Path.Combine(store.Generations.Directory, GenerationStore.Name(number));
                if (Directory.GetLastWriteTimeUtc(folder) < since.UtcDateTime) continue;
                throw new UnsafeOperationException($"Uncommitted generation {number} cannot be read, so its files cannot be ruled out; nothing was planned. ({ex.Message})");
            }
        }
        return paths;
    }

    /// <summary>
    /// Splits the planned files into those still in place and those already in this plan's trash, proving that every
    /// other file is exactly as reviewed. Anything else is a changed listing.
    /// </summary>
    private (IReadOnlyList<ReplicaPruneFile> Remaining, int AlreadyMoved) Remaining(ReplicaPrunePlan plan, IReadOnlyList<RemoteFile> listing)
    {
        string prefix = plan.TrashFolder + "/";
        var trashed = listing.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(f => f.Path[prefix.Length..], StringComparer.Ordinal);
        var live = Live(listing).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var remaining = new List<ReplicaPruneFile>();
        int moved = 0;
        var reconstructed = new List<RemoteFile>(live.Values);
        foreach (var file in plan.Files)
        {
            bool inPlace = live.TryGetValue(file.Path, out var now) && now.Size == file.Size && now.ModTime == file.ModTime;
            bool inTrash = trashed.TryGetValue(file.Path, out var thrown) && thrown.Size == file.Size;
            if (inPlace && !inTrash) remaining.Add(file);
            else if (inTrash && !live.ContainsKey(file.Path)) { moved++; reconstructed.Add(new(file.Path, file.Size, file.ModTime)); }
            else throw new UnsafeOperationException($"{file.Path} changed at the destination since the review; preview the cleanup again.");
        }
        if (Digest(reconstructed) != plan.ListingDigest)
            throw new UnsafeOperationException("Files at the destination changed since the review; preview the cleanup again. Reviewed files already moved stay in the trash folder.");
        return (remaining, moved);
    }

    private static IReadOnlyList<RemoteFile> Live(IEnumerable<RemoteFile> listing) =>
        listing.Where(f => !f.Path.Equals(TrashRoot, StringComparison.Ordinal) && !f.Path.StartsWith(TrashRoot + "/", StringComparison.Ordinal))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();

    private static string Digest(IEnumerable<RemoteFile> files) => Hashing.Sha256Hex(Encoding.UTF8.GetBytes(string.Join('\n',
        files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Path}\t{f.Size}\t{f.ModTime.UtcTicks}")))));

    private static string IntegrityOf(ReplicaPrunePlan plan) => Hashing.Sha256Hex(Json.Serialize(plan with { Integrity = "" }));

    private static void Validate(ReplicaPrunePlan plan)
    {
        if (plan.Format != ReplicaPrunePlan.CurrentFormat || plan.Files is null || plan.Id is null || plan.Target is null || plan.Destination is null
            || plan.ListingDigest is null || plan.SnapshotDigest is null || plan.Integrity != IntegrityOf(plan)
            || BatchTime(plan.Id) is null || plan.GraceDays is < 1 or > 3650)
            throw new InvalidDataException("The cleanup plan is incomplete, edited or from another version; preview again.");
        foreach (var file in plan.Files)
        {
            if (file is null || file.Path is null || file.Size < 0 || file.Category is not (Unreferenced or History)
                || file.Path.StartsWith('/') || file.Path.Split('/').Any(s => s is "" or "." or "..")
                || file.Path.StartsWith(TrashRoot + "/", StringComparison.Ordinal))
                throw new InvalidDataException("The cleanup plan contains an unsafe path; preview again.");
        }
    }

    private static string ShortId() => Guid.NewGuid().ToString("N")[..12];

    private static string Marker(DateTimeOffset at) => MovedMarker + at.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset? MarkerTime(string name) =>
        name.StartsWith(MovedMarker, StringComparison.Ordinal) && name.Length == MovedMarker.Length + 19
        && DateTimeOffset.TryParseExact(name[MovedMarker.Length..], "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    /// <summary>A review ID, and so its trash batch name, starts with when the review was made: <c>yyyyMMddTHHmmssZ-id</c>.</summary>
    internal static DateTimeOffset? BatchTime(string name) =>
        name.Length > 17 && name[16] == '-' && DateTimeOffset.TryParseExact(name[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    /// <summary>A version-history attempt folder is <c>yyyyMMddTHHmmssfffZ-id</c>, as the copier names it.</summary>
    internal static DateTimeOffset? AttemptTime(string name) =>
        name.Length > 19 && DateTimeOffset.TryParseExact(name[..19], "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    public static string Render(ReplicaPrunePlan plan)
    {
        var text = new StringBuilder();
        long bytes = plan.Files.Sum(f => f.Size);
        text.AppendLine(CultureInfo.InvariantCulture, $"Cloud copy '{plan.Target}' ({plan.Destination}) against committed generation {plan.Generation}: {plan.Listed} file(s) listed.");
        foreach (var group in plan.Files.GroupBy(f => f.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
            text.AppendLine(CultureInfo.InvariantCulture, $"  {group.Key}: {group.Count()} file(s), {StorageVolume.Gb(group.Sum(f => f.Size))}");
        string history = plan.HistoryOlderThanDays is { } d
            ? string.Create(CultureInfo.InvariantCulture, $"; version history newer than {d} day(s).")
            : "; all version history (add --history-older-than DAYS to include it).";
        text.AppendLine(CultureInfo.InvariantCulture, $"Kept: every referenced file, files changed in the last {plan.GraceDays} day(s) and files of recent unfinished switches{history}");
        if (plan.LeftAlone > 0) text.AppendLine(CultureInfo.InvariantCulture, $"Left alone: {plan.LeftAlone} file(s) that are not clip media or not dated history.");
        text.Append(CultureInfo.InvariantCulture, $"Apply moves these {plan.Files.Count} file(s) ({StorageVolume.Gb(bytes)}) into {plan.TrashFolder}/ at the destination; nothing is deleted until you purge the trash.");
        return text.ToString();
    }
}

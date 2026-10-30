using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

internal sealed record ReplicaRetryPlan(long Generation, long Revision, string SnapshotDigest, IReadOnlyList<string> Targets);

/// <summary>A generation-bound background copy. Reads ownership but never acquires, changes or releases it.</summary>
internal sealed class ReadyReplicaRetry(MedalSyncConfig config, LocalState state, CoordinationStore store,
    Action requireIdle, Action<IReadOnlyList<string>>? execute = null, Action<SessionProgress>? progress = null)
{
    public ReplicaRetryPlan Plan(string? targetName = null)
    {
        requireIdle();
        var journal = store.ReadJournal();
        if (journal.State != OwnershipState.Clean) throw new UnsafeOperationException("Cloud retry requires a Ready library.");
        var pending = StorageReplication.Pending(state, config.LibraryId).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = config.Replication?.Targets.Where(t => !t.Required && pending.Contains(t.Name)
            && (targetName is null || t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))).Select(t => t.Name).ToArray() ?? [];
        if (targetName is not null && targets.Length == 0) throw new UnsafeOperationException("--target must name a pending optional cloud copy.");
        return new(journal.CommittedGeneration, journal.Revision, store.Generations.Read(journal.CommittedGeneration).Snapshot.Digest(), targets);
    }

    public ReplicationResult Apply(ReplicaRetryPlan plan, string? targetName = null)
    {
        void Guard()
        {
            requireIdle();
            var current = store.ReadJournal();
            if (current.State != OwnershipState.Clean || current.Revision != plan.Revision || current.CommittedGeneration != plan.Generation)
                throw new OperationCanceledException("The library changed or another PC started; unfinished cloud copies remain pending.");
        }
        Guard();
        if (plan.Targets.Count == 0) return ReplicationResult.None;
        var snapshot = store.Generations.Read(plan.Generation).Snapshot;
        if (snapshot.Digest() != plan.SnapshotDigest) throw new InvalidDataException("The committed snapshot differs from the retry plan.");
        var originalPending = StorageReplication.Pending(state, config.LibraryId);
        var replication = new StorageReplication(config, state, Guard, execute, progress);
        try { return replication.RetryPending(snapshot, targetName); }
        catch
        {
            // Even a cancellation after a target's final check must not leave generation-stale success evidence.
            var pending = StorageReplication.Pending(state, config.LibraryId).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            string source = (config.PersistentMediaRoot is null ? config.ServerCapture : config.MountedCapture).Display;
            foreach (var target in config.Replication!.Targets.Where(t => plan.Targets.Contains(t.Name, StringComparer.OrdinalIgnoreCase)))
            {
                new ReplicationVerification(config, state, source, ReplicationPlan.Destination(target, config.LibraryId),
                    snapshot.Digest(), [], DateTimeOffset.UtcNow).Invalidate();
                var original = originalPending.First(t => t.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
                pending[target.Name] = original with { LastAttempt = DateTimeOffset.UtcNow };
            }
            if (pending.Count > 0)
                AtomicFile.Replace(Path.Combine(state.Root, "replication", "pending.json"), Json.Serialize(new PendingReplicas(1, config.LibraryId, pending.Values.ToArray())));
            throw;
        }
    }
}

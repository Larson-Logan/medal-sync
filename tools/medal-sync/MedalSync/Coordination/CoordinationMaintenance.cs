namespace MedalSync.Coordination;

internal sealed partial class CoordinationStore
{
    private OwnershipRecord? maintenance;
    private bool maintenanceWorkStarted;

    internal bool IsCloud => cloud is not null;

    internal T RunMaintenance<T>(DeviceIdentity device, string command, string operation, string digest,
        Func<T> work, long? head = null, long? revision = null, TimeSpan? timeout = null, Func<T>? completed = null)
    {
        var lockTimeout = timeout ?? TimeSpan.FromSeconds(10);
        if (cloud is null)
        {
            using var writer = AcquireWriterLock(lockTimeout);
            return work();
        }

        string completion = Ownership.MaintenanceCompletion(digest);
        bool IsCompleted(OwnershipRecord journal) => journal.State == OwnershipState.Clean && journal.Reason == completion
            && (head is null || journal.CommittedGeneration == head) && (revision is null || journal.Revision == revision + 2);
        if (completed is not null && IsCompleted(ReadJournal())) return completed();

        bool resumed = false;
        var held = Update(current =>
        {
            if (head is not null && current.CommittedGeneration != head)
                throw new UnsafeOperationException("The committed library changed since the review. Preview again.");
            if (current.State == OwnershipState.Maintenance && current.IsOwnedBy(device, operation)
                && current.Reason == command && current.OperationDigest == digest
                && (revision is null || current.Revision == revision + 1))
            {
                resumed = true;
                return current;
            }
            if (revision is not null && current.Revision != revision)
                throw new UnsafeOperationException("The library changed since the review. Preview again.");
            return Ownership.BeginMaintenance(current, device, operation, command, digest, DateTimeOffset.UtcNow);
        }, lockTimeout);
        maintenance = held;
        try
        {
            T result;
            try { result = work(); }
            catch (UnsafeOperationException) when (!resumed && !maintenanceWorkStarted)
            {
                // A failed review left no maintenance work to recover. Release the exact lease it acquired.
                Update(current => current == held
                    ? Ownership.EndMaintenance(current, device, operation, DateTimeOffset.UtcNow, completed: false)
                    : throw new OwnershipConflictException("Maintenance ownership changed."), lockTimeout);
                throw;
            }
            try
            {
                Update(current => current == held ? Ownership.EndMaintenance(current, device, operation, DateTimeOffset.UtcNow)
                    : throw new OwnershipConflictException("Maintenance ownership changed before release."), lockTimeout);
            }
            catch (IOException)
            {
                var observed = ReadJournal();
                if (observed.State != OwnershipState.Clean || observed.Revision != held.Revision + 1
                    || observed.CommittedGeneration != held.CommittedGeneration || observed.Reason != completion)
                    throw;
                return result;
            }
            if (ReadJournal().State != OwnershipState.Clean)
                throw new StorageBusyException("Maintenance completed and another PC has since taken ownership. Check library status.");
            return result;
        }
        finally { maintenance = null; maintenanceWorkStarted = false; }
    }

    internal OwnershipRecord ReadMaintenanceJournal()
    {
        var journal = ReadJournal();
        if (maintenance is null) return journal;
        if (journal != maintenance) throw new OwnershipConflictException("This PC lost the maintenance lease. Work stopped.");
        return journal with { State = OwnershipState.Clean, Owner = null, SessionId = null, Revision = journal.Revision - 1 };
    }

    internal void RequireMaintenance()
    {
        if (cloud is null) return;
        if (maintenance is null || cloud.Read(refreshStorage: true).Value.Journal != maintenance)
            throw new OwnershipConflictException("This operation requires this PC's active maintenance lease.");
    }

    internal void BeginMaintenanceWork()
    {
        RequireMaintenance();
        maintenanceWorkStarted = true;
    }

    internal void RefreshHistory() => cloud?.Download();

    internal IReadOnlyList<CloudGenerationAttempt> IncompleteCloudAttempts() =>
        cloud?.IncompleteAttempts(Generations).ToArray() ?? [];

    internal string? CloudStorageIdentity() => cloud?.StorageIdentity();

    internal void ApplyCloudRetention(GenerationRetention record, IEnumerable<long> generations, Action guard)
    {
        var held = maintenance ?? throw new InvalidOperationException("Cloud retention requires a maintenance lease.");
        cloud!.PublishRetention(record, held);
        foreach (long generation in generations)
        {
            cloud.RemoveRetainedGeneration(generation, guard);
        }
        RefreshHistory();
    }
}

using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Session;
using MedalSync.Storage;

namespace MedalSync.Platform;

/// <summary>Idle pre-copy uses the same conflict policy and verification receipts as finish; never deletes local media.</summary>
internal sealed class IdleMediaSync(MedalSyncConfig config, LocalState state, IClock clock,
    Action<IReadOnlyList<string>>? execute = null, IAssetChecker? primary = null)
{
    private readonly SettledMedia _settled = new();

    public bool Sync(LibrarySnapshot snapshot, Action requireIdle)
    {
        requireIdle();
        var source = config.MountedCapture;
        var files = ReplicationPlan.Files(snapshot, config.Capture, source, new FileSystemProbe());
        List<IDisposable> leases = [];
        List<string> ready = [];
        try
        {
            foreach (string file in files)
            {
                requireIdle();
                if (_settled.TryLease(source.Append(file.Split('/')).Display, clock.Now) is not { } lease) continue;
                leases.Add(lease);
                ready.Add(file);
            }
            if (ready.Count > 0 && config.Replication is not null)
                new StorageReplication(config, state, requireIdle, execute,
                    primary: primary ?? PrimaryStorage.Checker(config, state, requireIdle), idle: true).PublishIdle(snapshot, ready);
            requireIdle();
            return ready.Count == files.Count;
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
}

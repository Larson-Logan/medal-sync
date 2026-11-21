using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <param name="Downloaded">Files moved into the persistent local copy by this run.</param>
/// <param name="Remaining">Referenced files still missing locally when the run ended.</param>
/// <param name="Stopped">Why the run ended early, or null when nothing is left to download.</param>
internal sealed record PrefetchResult(long Generation, int Downloaded, long Bytes, int Remaining, long RemainingBytes, string? Stopped);

/// <summary>
/// Downloads clips another PC published into this PC's persistent local copy while it is idle, so the next launch
/// finds them already there. It never takes ownership, writes to the journal or deletes library media: it only reads
/// the newest committed, verified generation; downloads into a staging folder beside the local copy; byte-checks
/// each file against primary storage and the size the generation's asset manifest recorded; then moves it into the
/// local copy, never replacing a file that is already there. Launch still hydrates and verifies as before.
/// </summary>
internal sealed class MediaPrefetch(MedalSyncConfig config, LocalState state, CoordinationStore store, DeviceIdentity device,
    Action requireIdle, Action<IReadOnlyList<string>>? execute = null, Func<string, VolumeInfo?>? volumes = null)
{
    /// <summary>Beside the capture folder in the local copy, so a verified file moves into place on the same disk.</summary>
    public const string StagingFolder = ".medal-sync-prefetch";
    internal const int BatchFiles = 20;
    internal const long BatchBytes = 2L << 30;

    /// <summary>Prefetch never uses more than this, even when <c>replication.bandwidthLimitMiB</c> allows foreground copies more.</summary>
    internal const int BackgroundLimitMiB = 2;
    private readonly IFileProbe _probe = new FileSystemProbe();
    private readonly Func<string, VolumeInfo?> _volumes = volumes ?? StorageVolume.Describe;

    /// <summary>Off unless there is a persistent local copy; then on unless <c>replication.prefetchWhileIdle</c> is false.</summary>
    public static bool Enabled(MedalSyncConfig config) =>
        config.Replication is { LocalCopyRoot: not null } replication && replication.PrefetchWhileIdle != false;

    private string LocalRoot => config.Replication?.LocalCopyRoot ?? throw new UnsafeOperationException("Prefetch needs a persistent local copy (replication.localCopyRoot).");

    public string Staging => WindowsPath.Parse(LocalRoot).Append(StagingFolder).Display;

    /// <summary>What a run would download now, newest first, without downloading anything.</summary>
    public (long Generation, IReadOnlyList<(string Relative, long Size)> Files) Plan()
    {
        RequireSeparateStaging();
        requireIdle();
        var journal = RequireNotOwned();
        // Read verifies the generation's files against its manifest; a generation that fails is never prefetched from.
        var generation = store.Generations.Read(journal.CommittedGeneration);
        var local = config.MountedCapture;
        if (!File.Exists(local.Append(MedalSyncConfig.StorageMarker).Display))
            throw new UnsafeOperationException("This PC's persistent local copy is not set up yet; it is claimed at the first launch. Nothing was downloaded.");
        if (File.ReadAllText(local.Append(MedalSyncConfig.StorageMarker).Display).Trim() != config.LibraryId)
            throw new UnsafeOperationException("The persistent local copy belongs to another library; nothing was downloaded.");
        var recorded = generation.Assets.Present
            .Select(a => (Entry: a, Ok: WindowsPath.TryParse(a.Path, out var path, out _) && path.IsWithin(config.Capture), Path: path))
            .Where(a => a.Ok)
            .GroupBy(a => string.Join('/', a.Path!.Segments.Skip(config.Capture.Segments.Count)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Entry, StringComparer.OrdinalIgnoreCase);
        var files = new List<(string Relative, long Size, DateTimeOffset At)>();
        foreach (var (relative, _) in ReplicationPlan.Media(generation.Snapshot, config.Capture))
        {
            // Only files the generation recorded as present on primary storage, with the size it recorded.
            if (!recorded.TryGetValue(relative, out var entry) || PresentLocally(relative)) continue;
            files.Add((relative, entry.Length, new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteUtc, DateTimeKind.Utc))));
        }
        return (journal.CommittedGeneration, files.OrderByDescending(f => f.At).ThenBy(f => f.Relative, StringComparer.Ordinal)
            .Select(f => (f.Relative, f.Size)).ToArray());
    }

    public PrefetchResult Run()
    {
        var (generation, files) = Plan();
        // A partial download from an interrupted run is discarded: only checked files ever leave the staging folder.
        Discard();
        int downloaded = 0;
        long bytes = 0;
        int next = 0;
        string? stopped = null;
        try
        {
            while (next < files.Count)
            {
                var batch = Batch(files, next);
                if (!HasRoom(batch.Sum(f => f.Size))) { stopped = "space"; break; }
                requireIdle();
                RequireSameGeneration(generation);
                Transfer(batch.Select(f => f.Relative).ToArray());
                requireIdle();
                RequireSameGeneration(generation);
                foreach (var (relative, size) in batch)
                {
                    if (!Place(relative, size)) continue;
                    downloaded++;
                    bytes += size;
                }
                next += batch.Count;
            }
        }
        catch (OperationCanceledException ex)
        {
            stopped = ex.Message;
        }
        finally
        {
            // A file rclone still held when it was stopped is discarded by the next run instead.
            try { Discard(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stopped ??= "staging cleanup"; }
        }
        var left = files.Where(f => !PresentLocally(f.Relative)).ToArray();
        return new(generation, downloaded, bytes, left.Length, left.Sum(f => f.Size), left.Length == 0 ? null : stopped ?? "incomplete");
    }

    private static IReadOnlyList<(string Relative, long Size)> Batch(IReadOnlyList<(string Relative, long Size)> files, int start)
    {
        var batch = new List<(string, long)>();
        long total = 0;
        for (int i = start; i < files.Count && batch.Count < BatchFiles; i++)
        {
            if (batch.Count > 0 && total + files[i].Size > BatchBytes) break;
            batch.Add(files[i]);
            total += files[i].Size;
        }
        return batch;
    }

    private OwnershipRecord RequireNotOwned()
    {
        var journal = store.ReadJournal();
        if (journal.Owner?.DeviceId == device.DeviceId)
            throw new UnsafeOperationException("This PC holds the library; its session downloads what it needs. Nothing was prefetched.");
        if (journal.State != OwnershipState.Clean && journal.Owner is null)
            throw new UnsafeOperationException("The ownership journal is not in a state prefetch understands; nothing was downloaded.");
        return journal;
    }

    private void RequireSameGeneration(long generation)
    {
        var journal = RequireNotOwned();
        if (journal.CommittedGeneration != generation)
            throw new OperationCanceledException("A newer library generation was published; prefetch starts again from it next time.");
    }

    /// <summary>Downloads into staging, then byte-compares staging with primary storage. Any difference stops the run.</summary>
    private void Transfer(IReadOnlyList<string> files)
    {
        Directory.CreateDirectory(Staging);
        RequireOrdinary(Staging);
        string configFile = config.PrimaryIsRclone
            ? Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!)
            : Path.Combine(state.Root, "rclone-empty.conf");
        Directory.CreateDirectory(state.Root);
        string list = Path.Combine(state.Root, "prefetch-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(list, files, new System.Text.UTF8Encoding(false));
            int limit = Math.Min(config.Replication?.BandwidthLimitMiB ?? BackgroundLimitMiB, BackgroundLimitMiB);
            foreach (string command in new[] { "copy", "check" })
            {
                requireIdle();
                var arguments = ReplicationPlan.Arguments(command, config.PrimaryCapture, StagingCapture, list, configFile, limit).ToList();
                arguments[arguments.IndexOf("--transfers") + 1] = "1";
                if (execute is not null) execute(arguments);
                else new RcloneRunner(config.ExpandedRclonePath).Run(arguments,
                    TimeSpan.FromMinutes(config.Replication?.TimeoutMinutes ?? 240), requireIdle, lowPriority: true);
            }
        }
        finally { File.Delete(list); }
    }

    private string StagingCapture => WindowsPath.Parse(Staging).Append(config.Capture.Segments.ToArray()).Display;

    /// <summary>Moves one checked file into the local copy. False when it was already there; that copy is never replaced.</summary>
    private bool Place(string relative, long size)
    {
        var staged = WindowsPath.Parse(StagingCapture).Append(relative.Split('/'));
        var info = new FileInfo(staged.Display);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length != size)
            throw new IOException("A prefetched clip does not match the size its generation recorded; nothing more was moved.");
        var target = config.MountedCapture.Append(relative.Split('/'));
        if (PresentLocally(relative)) return false;
        RequireOrdinary(Path.GetDirectoryName(target.Display)!);
        Directory.CreateDirectory(Path.GetDirectoryName(target.Display)!);
        try { File.Move(staged.Display, target.Display, overwrite: false); }
        catch (IOException) when (File.Exists(target.Display)) { return false; }
        return true;
    }

    private bool PresentLocally(string relative)
    {
        var found = _probe.Probe(config.MountedCapture.Append(relative.Split('/')), config.MountedCapture);
        if (found.HasReparsePoint || found.IsDirectory) throw new IOException("The persistent local copy contains a link or folder in place of a clip; prefetch was refused.");
        return found.Exists;
    }

    private bool HasRoom(long bytes)
    {
        var volume = StorageVolume.RequirePresent(LocalRoot, "persistent clip storage", _volumes);
        long reserve = (long)config.Mount.MinFreeGiB << 30;
        return bytes <= volume.FreeBytes - reserve;
    }

    /// <summary>
    /// Staging is deleted recursively, so it must never contain the local capture folder (a capture folder such as
    /// <c>M:\.medal-sync-prefetch\Clips</c>) nor lie inside it.
    /// </summary>
    private void RequireSeparateStaging()
    {
        var staging = WindowsPath.Parse(Staging);
        if (config.MountedCapture.IsWithin(staging) || staging.IsWithin(config.MountedCapture))
            throw new UnsafeOperationException($"Prefetch stages downloads in '{Staging}', which overlaps the persistent local copy's "
                + "capture folder. Choose a capture folder outside it; nothing was downloaded or removed.");
    }

    private void Discard()
    {
        RequireSeparateStaging();
        if (!Directory.Exists(Staging)) return;
        RequireOrdinary(Staging);
        Directory.Delete(Staging, recursive: true);
    }

    private static void RequireOrdinary(string folder)
    {
        var path = WindowsPath.Parse(folder);
        var current = WindowsPath.Parse(path.Root);
        foreach (string segment in path.Segments)
        {
            current = current.Append(segment);
            if (Directory.Exists(current.Display) && File.GetAttributes(current.Display).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The persistent local copy contains a link or junction; prefetch was refused.");
        }
    }
}

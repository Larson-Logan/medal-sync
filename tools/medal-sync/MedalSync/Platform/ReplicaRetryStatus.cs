using MedalSync.Config;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Platform;

internal sealed record ReplicaRetryReport(int Format, string LibraryId, long Generation, bool Running, string Message,
    DateTimeOffset ConfirmedAt, string? Target = null);

/// <summary>Advisory retry progress, separate from the supervisor so Ready remains available to other PCs.</summary>
internal sealed class ReplicaRetryStatus(LocalState state, string libraryId, long generation) : IDisposable
{
    private readonly object _gate = new();
    private ReplicaRetryReport _report = new(1, libraryId, generation, true, "Retrying pending cloud copies.", DateTimeOffset.UtcNow);
    private Timer? _heartbeat;
    private string PathName => Path.Combine(state.Root, "replication", "retry-status.json");

    public void Start()
    {
        Write();
        _heartbeat = new Timer(_ => Write(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }
    public void Report(SessionProgress progress)
    {
        lock (_gate) { _report = _report with { Message = progress.NextAction, Target = progress.Replica }; Write(); }
    }
    public void Complete(string message)
    {
        lock (_gate) { _report = _report with { Running = false, Message = message, Target = null }; Write(); }
    }
    private void Write()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                _report = _report with { ConfirmedAt = DateTimeOffset.UtcNow };
                AtomicFile.Replace(PathName, Json.Serialize(_report));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Advisory. */ }
        }
    }
    public static ReplicaRetryReport? Read(LocalState state, string libraryId, DateTimeOffset now)
    {
        try
        {
            var report = Json.ReadFile<ReplicaRetryReport>(Path.Combine(state.Root, "replication", "retry-status.json"), "Cloud retry progress");
            if (report.Format != 1 || report.LibraryId != libraryId || report.Generation < 0 || report.Message is null
                || report.ConfirmedAt > now.AddSeconds(5) || now - report.ConfirmedAt > TimeSpan.FromHours(1)) return null;
            return report.Running && now - report.ConfirmedAt > TimeSpan.FromSeconds(20)
                ? report with { Running = false, Message = "Cloud retry progress is stale; check pending copies and retry while Ready." } : report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }
    public void Dispose() => _heartbeat?.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

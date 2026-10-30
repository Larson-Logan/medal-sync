using System.Runtime.Versioning;

namespace MedalSync.Platform;

/// <summary>Separate from ownership: a local launch cancels a retry before using its media. Never holds the journal writer lock.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ReplicaRetryInterlock : IDisposable
{
    private const string GateName = @"Local\LarsonServer.MedalSync.ReplicaRetry";
    private const string StopName = @"Local\LarsonServer.MedalSync.StopReplicaRetry";
    private readonly Mutex _gate;
    private readonly EventWaitHandle _stop;
    private bool _held;

    private ReplicaRetryInterlock(bool launch, string suffix)
    {
        _gate = new(false, GateName + suffix);
        _stop = new(false, EventResetMode.ManualReset, StopName + suffix);
        try
        {
            if (launch) _stop.Set();
            try { _held = _gate.WaitOne(launch ? TimeSpan.FromSeconds(5) : TimeSpan.Zero); }
            catch (AbandonedMutexException) { _held = true; }
            if (!_held) throw new UnsafeOperationException(launch
                ? "Cloud retry is stopping. Open Medal again in a moment."
                : "A cloud retry or library operation is already running on this PC.");
            if (!launch) _stop.Reset();
        }
        catch { Dispose(); throw; }
    }

    public static ReplicaRetryInterlock Retry(string suffix = "") => new(false, suffix);
    public static ReplicaRetryInterlock Launch(string suffix = "") => new(true, suffix);
    public void RequireContinuing()
    {
        if (_stop.WaitOne(0)) throw new OperationCanceledException("Cloud retry stopped for launch; unfinished cloud copies remain pending.");
    }
    public void Dispose()
    {
        if (_held) { _held = false; _gate.ReleaseMutex(); }
        _gate.Dispose();
        _stop.Dispose();
    }
}

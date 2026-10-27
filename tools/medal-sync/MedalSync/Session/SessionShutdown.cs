namespace MedalSync.Session;

/// <summary>One Windows end-session attempt. A query alone must not stop a session.</summary>
internal sealed class SessionShutdown(IClock clock, TimeSpan? budget = null)
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(25);
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _requested = new(false);
    private readonly ManualResetEventSlim _completed = new(false);
    private bool _querying;
    private DateTimeOffset? _deadline;

    public bool SuppressUi { get { lock (_gate) return _querying || _deadline is not null; } }
    public bool Ending { get { lock (_gate) return _deadline is not null; } }
    public TimeSpan Budget => budget ?? DefaultBudget;

    public void Query() { lock (_gate) _querying = true; }

    public void CancelQuery() { lock (_gate) { if (_deadline is null) _querying = false; } }

    public void RequestEnd()
    {
        lock (_gate)
        {
            _deadline ??= clock.Now + Budget;
            _requested.Set();
        }
    }

    /// <summary>Serialize the final launch decision against a confirmed shutdown.</summary>
    public bool TryLaunch(Action launch)
    {
        lock (_gate)
        {
            if (_querying || _deadline is not null) return false;
            launch();
            return true;
        }
    }

    public void RequireTime()
    {
        lock (_gate)
        {
            if (_deadline is { } end && clock.Now >= end)
                throw new OperationCanceledException("Windows shutdown budget expired; ownership and unsent cache are retained for next sign-in.");
        }
    }

    public void Pause(TimeSpan interval)
    {
        // Wake the regular supervisor immediately when WM_ENDSESSION arrives.
        // Synthetic clocks still advance deterministically in tests.
        if (Ending) clock.Sleep(interval < TimeSpan.FromMilliseconds(250) ? interval : TimeSpan.FromMilliseconds(250));
        else if (clock is SystemClock) _requested.Wait(interval);
        else clock.Sleep(interval);
    }

    public void Complete() => _completed.Set();
    public bool WaitForCompletion() => _completed.Wait(Budget);
}

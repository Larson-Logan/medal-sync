using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Cli;

/// <summary>
/// The local half of a background operation while the library is Ready: it holds the retry interlock (so a launch on
/// this PC cancels it) and stops for Ctrl+C, Windows shutdown, setup, a configuration change or a running Medal.
/// It never takes ownership; callers check the journal themselves.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ReadyOperation : IDisposable
{
    private readonly LocalState _state;
    private readonly MedalSyncConfig _config;
    private readonly string _fingerprint;
    private readonly ReplicaRetryInterlock? _interlock;
    private readonly SessionShutdown _shutdown = new(new SystemClock());
    private readonly WindowsSessionEnd? _ending;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConsoleCancelEventHandler? _cancel;
    private readonly string _name;
    private readonly string _remains;
    private readonly string _quitMedal;
    private readonly string _changedConfig;

    /// <param name="name">Starts each stop message, for example "Cloud retry".</param>
    /// <param name="remains">Ends each stop message, for example "unfinished copies remain pending".</param>
    /// <param name="active">False for a preview: nothing is held and only the checks run.</param>
    public ReadyOperation(LocalState state, MedalSyncConfig config, bool active, string name, string remains, string quitMedal, string changedConfig)
    {
        (_state, _config, _name, _remains, _quitMedal, _changedConfig) = (state, config, name, remains, quitMedal, changedConfig);
        _fingerprint = Hashing.Sha256File(state.ConfigPath);
        if (!active) return;
        try
        {
            _interlock = ReplicaRetryInterlock.Retry();
            _ending = new WindowsSessionEnd(_shutdown, _ => { }, setOrder: false);
            _cancel = (_, e) => { e.Cancel = true; _cancellation.Cancel(); };
            Console.CancelKeyPress += _cancel;
        }
        catch { Dispose(); throw; }
    }

    public void Guard()
    {
        _cancellation.Token.ThrowIfCancellationRequested();
        SetupMaintenance.RequireNotRunning();
        _interlock?.RequireContinuing();
        if (_shutdown.Ending) throw new OperationCanceledException($"{_name} stopped for Windows shutdown; {_remains}.");
        if (SessionCommands.AnotherInstanceRunning() || Directory.Exists(_config.DriveRoot.Display))
            throw new OperationCanceledException($"{_name} stopped for a local library operation; {_remains}.");
        if (Hashing.Sha256File(_state.ConfigPath) != _fingerprint) throw new OperationCanceledException(_changedConfig);
        // No library database or new identity is needed; only observe whether Medal is running.
        foreach (string name in new[] { "Medal", "MedalEncoder", "MedalRecorder" })
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(name);
            bool running = processes.Length != 0;
            foreach (var process in processes) process.Dispose();
            if (running) throw new OperationCanceledException(_quitMedal);
        }
    }

    public void Dispose()
    {
        if (_cancel is not null) Console.CancelKeyPress -= _cancel;
        _ending?.Dispose();
        _interlock?.Dispose();
        _shutdown.Complete();
        _cancellation.Dispose();
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using LarsonServer.MedalSync.Config;
using LarsonServer.MedalSync.Coordination;
using LarsonServer.MedalSync.Session;
using LarsonServer.MedalSync.Storage;

namespace LarsonServer.MedalSync.Platform;

/// <summary>
/// The rclone + WinFsp drive for one session. Not exercised by automated tests:
/// it needs WinFsp and the server, and Gate B of the plan is its acceptance test.
/// Its decisions (arguments, drain verdicts, capacity) are the tested pure parts.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RcloneClipMount(MedalSyncConfig config, LocalState state, double bitrateMbps, IClock clock) : IClipMount
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    private Process? _process;
    private string? _session;
    private RcloneControl? _control;

    private string DriveRoot => config.DriveRoot.Display;

    public bool IsRunning => (_process is { HasExited: false }) || Directory.Exists(DriveRoot);

    public void Start(string session, bool forRecording)
    {
        if (Directory.Exists(DriveRoot))
        {
            throw new UnsafeOperationException($"{DriveRoot} is already in use. medal-sync never picks a different drive letter; free {DriveRoot} first.");
        }
        string rclone = config.ExpandedRclonePath;
        if (!File.Exists(rclone)) throw new UnsafeOperationException($"rclone is not installed at '{rclone}'.");
        string cache = config.SessionCacheDirectory(session);
        // Finishing uploads needs no new space: the pending files are already on the SSD.
        if (forRecording) Admit(cache);
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(state.LogDirectory);
        var secret = state.SessionSecret(session, clock.Now);
        var start = new ProcessStartInfo(rclone)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (string argument in MountPlan.Arguments(config.MountSettings(session, Path.Combine(state.LogDirectory, $"rclone-{session}.log"))))
        {
            start.ArgumentList.Add(argument);
        }
        // Only the settings chosen here: a user-level RCLONE_* variable (for
        // example one disabling control-API authentication) must not leak in.
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }
        start.Environment[MountPlan.ControlUserVariable] = secret.User;
        start.Environment[MountPlan.ControlPasswordVariable] = secret.Password;
        // Never read a personal rclone.conf: the local backend needs no remotes.
        start.Environment["RCLONE_CONFIG"] = Path.Combine(state.Root, "rclone-empty.conf");
        Attach(session);
        _process = Process.Start(start) ?? throw new UnsafeOperationException("rclone did not start.");
        var deadline = clock.Now + StartTimeout;
        while (!(Directory.Exists(DriveRoot) && Control()?.Call("core/version") is not null))
        {
            if (_process.HasExited) throw new UnsafeOperationException($"rclone exited with code {_process.ExitCode}; see {state.LogDirectory}.");
            if (clock.Now >= deadline) throw new UnsafeOperationException($"{DriveRoot} did not appear within {StartTimeout.TotalSeconds:0} seconds.");
            clock.Sleep(TimeSpan.FromMilliseconds(500));
        }
        // Read every directory once so Medal's existence checks are answered from
        // memory, even if the server becomes unreachable during the session.
        if (Control()?.Call("vfs/refresh", "{\"recursive\":\"true\"}", TimeSpan.FromMinutes(10)) is null)
        {
            throw new UnsafeOperationException("rclone could not read the clip folders on the server.");
        }
    }

    private void Admit(string cache)
    {
        var drive = new DriveInfo(Path.GetPathRoot(cache)!);
        long pending = Directory.Exists(cache)
            ? new DirectoryInfo(cache).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;
        var verdict = CapacityAdmission.Evaluate(new CapacityInputs(drive.AvailableFreeSpace, (long)config.Mount.MinFreeGiB << 30,
            bitrateMbps, TimeSpan.FromHours(config.MaxSessionHours), pending));
        if (!verdict.Admitted) throw new UnsafeOperationException(verdict.Summary);
    }

    public MountHealth Health()
    {
        var control = Control();
        if (control is null) return new MountHealth(false, "This PC does not know the drive's session.");
        string? stats = control.Call("vfs/stats");
        if (stats is null) return new MountHealth(false, "rclone's local status API is not answering. Upload status is unknown; keep Medal open while it recovers.", Retryable: true);
        try
        {
            using var document = JsonDocument.Parse(stats);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("fs", out var fs) || fs.ValueKind != JsonValueKind.String
                || !VfsStatus.SameRemote(fs.GetString()!, config.Remote))
            {
                return new MountHealth(false, $"{DriveRoot} is not serving {config.Remote}.");
            }
        }
        catch (JsonException)
        {
            return new MountHealth(false, "rclone answered with something that is not its status.");
        }
        string marker = config.Capture.Append(MedalSyncConfig.StorageMarker).Display;
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != config.LibraryId)
        {
            return new MountHealth(false, $"{marker} does not identify library {config.LibraryId}.");
        }
        if (!Directory.Exists(config.Capture.Display)) return new MountHealth(false, $"{config.Capture.Display} is missing.");
        return new MountHealth(true, $"{DriveRoot} serves {config.Remote}.");
    }

    public DrainVerdict Drain()
    {
        var control = Control();
        if (control is null) return DrainVerdict.Unknown("This PC does not know the drive's session.");
        return VfsStatus.Evaluate(control.Call("vfs/stats"), control.Call("vfs/queue"), config.Remote);
    }

    public bool Stop()
    {
        Control()?.Call("core/quit");
        var deadline = clock.Now + StopTimeout;
        while (IsRunning && clock.Now < deadline) clock.Sleep(TimeSpan.FromMilliseconds(500));
        return !IsRunning;
    }

    /// <summary>
    /// Deletes a finished session's cache folder and control login. Only called
    /// after its uploads drained and the drive stopped, so the folder holds
    /// nothing the server lacks.
    /// </summary>
    public void Forget(string session)
    {
        if (!CoordinationStore.IsValidId(session)) throw new ArgumentException("Invalid session ID.", nameof(session));
        if (IsRunning) throw new IOException($"{DriveRoot} is still mounted; the session's cache is kept.");
        string cache = config.SessionCacheDirectory(session);
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        state.ForgetSession(session);
        if (_session != session) return;
        _control?.Dispose();
        _control = null;
        _session = null;
    }

    private RcloneControl? Control()
    {
        if (_session is null) return null;
        if (_control is null)
        {
            var secret = state.SessionSecret(_session, clock.Now);
            _control = new RcloneControl(config.Mount.ControlPort, secret.User, secret.Password);
        }
        return _control;
    }

    /// <summary>Lets a restarted companion talk to the drive of a session it already owns.</summary>
    public void Attach(string session)
    {
        if (_session == session) return;
        _control?.Dispose();
        _control = null;
        _session = session;
    }
}

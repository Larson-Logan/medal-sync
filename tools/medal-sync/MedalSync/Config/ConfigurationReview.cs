using System.Security.Cryptography;
using MedalSync.Util;

namespace MedalSync.Config;

/// <summary>A read-only configuration proposal tied to the files actually reviewed.</summary>
internal sealed class ConfigurationReview
{
    private readonly byte[] _bytes;
    private readonly byte[]? _previous;
    private readonly string? _device;
    private readonly bool _newLibrary;
    public LocalState State { get; }
    public MedalSyncConfig Config => Json.Deserialize<MedalSyncConfig>(_bytes, "Configuration proposal");
    public bool ReplacesExisting => _previous is not null;

    private ConfigurationReview(LocalState state, MedalSyncConfig config, bool newLibrary)
    {
        State = state;
        RequireOrdinaryPath(state.ConfigPath);
        RequireOrdinaryPath(Path.Combine(state.Root, "device.json"));
        _bytes = Json.Serialize(config);
        _previous = File.Exists(state.ConfigPath) ? File.ReadAllBytes(state.ConfigPath) : null;
        _device = Fingerprint(Path.Combine(state.Root, "device.json"));
        _newLibrary = newLibrary;
    }

    public static ConfigurationReview Prepare(LocalState state, MedalSyncConfig config, bool newLibrary,
        Action<MedalSyncConfig, bool> validateEnvironment, Action<MedalSyncConfig>? validateExisting = null)
    {
        config.Validate();
        var review = new ConfigurationReview(state, config, newLibrary);
        if (review._previous is not null)
        {
            var existing = Json.Deserialize<MedalSyncConfig>(review._previous, "Existing configuration");
            existing.Validate();
            if (newLibrary || existing.LibraryId != config.LibraryId || existing.AccountId != config.AccountId)
                throw new UnsafeOperationException("An existing setup keeps its account and library ID. Recover its original configuration instead of creating another identity.");
            if (validateExisting is not null) validateExisting(existing);
            else validateEnvironment(existing, false); // A new clean destination cannot conceal original ownership.
        }
        else if (File.Exists(Path.Combine(state.Root, "device.json")))
            throw new UnsafeOperationException("A device identity already exists without its configuration. Recover the original configuration before continuing.");
        RequireNoSessions(state);
        validateEnvironment(config, newLibrary);
        review.RequireUnchanged();
        return review;
    }

    public string Text => $"Configuration location: {State.ConfigPath}\r\n"
        + $"{(ReplacesExisting ? "Change existing setup (identity retained)" : _newLibrary ? "New library (not initialized)" : "Join existing library (not enrolled)")}\r\n"
        + $"Library: {Config.LibraryId}; account: {Config.AccountId}\r\n"
        + $"Storage mode: {Config.StorageMode}\r\nStorage: {Config.Remote}\r\nCoordination: {Config.CoordinationRoot}\r\n"
        + $"Capture: {Config.CaptureFolder} (drive {Config.Capture.Root})\r\nCache: {Config.CacheRoot}\r\n"
        + $"Cache limit: {Config.Mount.CacheMaxGiB} GiB; minimum free space: {Config.Mount.MinFreeGiB} GiB; planned session: {Config.MaxSessionHours} hours\r\n"
        + $"Persistent local copy: {Config.Replication?.LocalCopyRoot ?? "none"}\r\n"
        + $"Cloud targets: {string.Join(", ", Config.Replication?.Targets.Select(t => t.Name + " = " + t.Remote + " (" + Requirement(t.Required) + ")") ?? [])}\r\n"
        + TargetChanges()
        + "\r\n"
        + $"Provider credential file: {Config.Replication?.RcloneConfigPath ?? "none"} (authorization remains separate)\r\n"
        + $"Sync exclusions: {string.Join(", ", Config.SyncExclusions.Select(e => e.FolderId + " = " + e.Pattern))}\r\n\r\n"
        + (ReplacesExisting ? "Saving creates a backup before atomic replacement.\r\n" : "Saving creates configuration only; device identity is created by the existing enrollment workflow.\r\n")
        + "Next: quit Medal normally; preserve a verified database/media backup; verify assets and sync exclusions. "
        + (_newLibrary ? "Preview init only for this genuinely new library, then explicitly apply." : "Preview enroll for this existing library, then explicitly apply.")
        + " Authorize cloud providers separately with rclone config. No journal, mount, Medal database or startup entry is changed here.";

    private static string Requirement(bool required) => required ? "required" : "optional";

    private string TargetChanges()
    {
        if (_previous is null) return "";
        var previous = Json.Deserialize<MedalSyncConfig>(_previous, "Existing configuration").Replication?.Targets ?? [];
        var current = Config.Replication?.Targets ?? [];
        var changes = new List<string>();
        foreach (var target in previous)
        {
            var updated = current.FirstOrDefault(t => t.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
            if (updated is null) changes.Add($"Cloud target '{target.Name}' removed (was {Requirement(target.Required)}).");
            else if (updated.Required != target.Required)
                changes.Add($"Cloud target '{target.Name}' requirement changed: {Requirement(target.Required)} -> {Requirement(updated.Required)}.");
        }
        foreach (var target in current.Where(t => !previous.Any(p => p.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))))
            changes.Add($"Cloud target '{target.Name}' added ({Requirement(target.Required)}).");
        return changes.Count == 0 ? "" : string.Join("\r\n", changes) + "\r\n";
    }

    /// <summary>Explicit confirmation is mandatory. Call with the supervisor instance mutex held.</summary>
    public string? Save(bool confirmed, Action<MedalSyncConfig, bool> validateEnvironment, Action<MedalSyncConfig>? validateExisting = null)
    {
        if (!confirmed) return null;
        RequireUnchanged();
        if (_previous is not null)
        {
            var existing = Json.Deserialize<MedalSyncConfig>(_previous, "Existing configuration");
            if (validateExisting is not null) validateExisting(existing);
            else validateEnvironment(existing, false);
        }
        validateEnvironment(Config, _newLibrary);
        RequireUnchanged();
        Directory.CreateDirectory(State.Root);
        RequireUnchanged();
        string? backup = null;
        if (_previous is null)
        {
            // Atomic publication without overwriting a concurrently created configuration.
            string temp = Path.Combine(State.Root, ".config." + Guid.NewGuid().ToString("N") + ".tmp");
            try { AtomicFile.CreateNew(temp, _bytes); File.Move(temp, State.ConfigPath, overwrite: false); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        else
        {
            Directory.CreateDirectory(State.BackupDirectory);
            RequireOrdinaryPath(State.BackupDirectory);
            backup = Path.Combine(State.BackupDirectory, "config-" + Guid.NewGuid().ToString("N") + ".json");
            AtomicFile.CreateNew(backup, _previous);
            if (!File.ReadAllBytes(backup).SequenceEqual(_previous))
                throw new IOException("The configuration backup did not verify. The existing configuration is retained.");
            string temp = Path.Combine(State.Root, ".config." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                AtomicFile.CreateNew(temp, _bytes);
                RequireUnchanged();
                File.Replace(temp, State.ConfigPath, null);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        if (!File.ReadAllBytes(State.ConfigPath).SequenceEqual(_bytes))
            throw new IOException("The saved configuration did not verify. Keep its backup and review the setup again.");
        return backup;
    }

    private void RequireUnchanged()
    {
        RequireOrdinaryPath(State.ConfigPath);
        RequireOrdinaryPath(Path.Combine(State.Root, "device.json"));
        RequireOrdinaryPath(State.BackupDirectory);
        RequireNoSessions(State);
        if (Fingerprint(State.ConfigPath) != (_previous is null ? null : Convert.ToHexString(SHA256.HashData(_previous)))
            || Fingerprint(Path.Combine(State.Root, "device.json")) != _device)
            throw new UnsafeOperationException("Configuration or device identity changed after review. Review the current setup again before saving.");
    }

    private static void RequireNoSessions(LocalState state)
    {
        string sessions = Path.Combine(state.Root, "sessions");
        if (Directory.Exists(sessions) && Directory.EnumerateFiles(sessions, "*.json").Any())
            throw new UnsafeOperationException("An unfinished session is retained. Recover it and wait for Ready before changing configuration.");
    }

    private static string? Fingerprint(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;

    private static void RequireOrdinaryPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnsafeOperationException("The configuration state path contains a symbolic link or reparse point. Recover or choose an ordinary local state folder before saving.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}

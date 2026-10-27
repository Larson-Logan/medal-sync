using System.Security.Cryptography;
using MedalSync.Coordination;
using MedalSync.Util;

namespace MedalSync.Config;

internal sealed record DeviceFile(int Format, string DeviceId, string DisplayName);

/// <summary>Per-session control API login for this PC's rclone process.</summary>
internal sealed record SessionSecret(int Format, string Session, string User, string Password, DateTimeOffset CreatedAt);

/// <summary>
/// User state (normally <c>%USERPROFILE%\.medal-sync</c>): configuration, this PC's
/// identity, rollback backups, logs and per-session control logins. The folder
/// sits in the user's profile, which only this user (and administrators) can read.
/// </summary>
internal sealed class LocalState(string root)
{
    public static LocalState ForCurrentUser() => new(ResolveRoot(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    internal static string ResolveRoot(string executableDirectory, string userProfile, string localAppData)
    {
        string appDataRoot = Path.Combine(localAppData, "MedalSync");
        string legacyRoot = Path.Combine(localAppData, "LarsonServer", "MedalSync");
        if (!IsManagedInstall(executableDirectory) && InstalledRoot(executableDirectory) is { } portable) return portable;
        string profileRoot = Path.Combine(userProfile, ".medal-sync");
        // The Program Files installation never writes user data beside its binaries.
        // Prefer the explicit physical profile root and preserve older AppData setups.
        if (File.Exists(Path.Combine(profileRoot, "config.json")) || File.Exists(Path.Combine(profileRoot, "device.json"))) return profileRoot;
        if (File.Exists(Path.Combine(appDataRoot, "config.json")) || File.Exists(Path.Combine(appDataRoot, "device.json"))) return appDataRoot;
        if (File.Exists(Path.Combine(legacyRoot, "config.json")) || File.Exists(Path.Combine(legacyRoot, "device.json"))) return legacyRoot;
        return profileRoot;
    }

    internal static bool IsManagedInstall(string executableDirectory)
    {
        string marker = Path.Combine(executableDirectory, "setup-layout.json");
        if (!File.Exists(marker)) return false;
        var layout = Json.ReadFile<SetupLayout>(marker, "Windows installation layout");
        if (layout.Format != 1 || layout.Layout != "windows-setup") throw new InvalidDataException("Unsupported Windows installation layout.");
        return true;
    }

    private sealed record SetupLayout(int Format, string Layout);

    /// <summary>
    /// A configured installation keeps state beside its bin directory. This
    /// also supports a per-user location outside AppData, whose filesystem view
    /// can otherwise differ between packaged tools and the Windows desktop.
    /// Unconfigured build outputs retain the original per-user default.
    /// </summary>
    internal static string? InstalledRoot(string executableDirectory)
    {
        string bin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
        if (!string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase)) return null;
        string? root = Path.GetDirectoryName(bin);
        return root is not null && File.Exists(Path.Combine(root, "config.json")) && File.Exists(Path.Combine(root, "device.json"))
            ? root : null;
    }

    public string Root { get; } = root;

    public string ConfigPath => Path.Combine(Root, "config.json");

    public string BackupDirectory => Path.Combine(Root, "Backups");

    public string LogDirectory => Path.Combine(Root, "logs");

    private string SessionsDirectory => Path.Combine(Root, "sessions");

    /// <summary>
    /// This PC's identity, created once. The random ID is what ownership
    /// compares; the computer and user name it was made for must still match, so
    /// a profile copied to another PC cannot pass for the original owner.
    /// </summary>
    public DeviceIdentity Device() => Device($"{Environment.MachineName} ({Environment.UserName})");

    internal DeviceIdentity Device(string thisComputer)
    {
        string path = Path.Combine(Root, "device.json");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Root);
            var created = new DeviceFile(1, Guid.NewGuid().ToString("N"), thisComputer);
            try
            {
                AtomicFile.CreateNew(path, Json.Serialize(created));
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another instance created it first; use theirs.
            }
        }
        return ExistingDevice(thisComputer);
    }

    internal DeviceIdentity ExistingDevice() => ExistingDevice($"{Environment.MachineName} ({Environment.UserName})");

    private DeviceIdentity ExistingDevice(string thisComputer)
    {
        string path = Path.Combine(Root, "device.json");
        var file = Json.ReadFile<DeviceFile>(path, "Device identity");
        if (file.Format != 1 || !CoordinationStore.IsValidId(file.DeviceId) || string.IsNullOrWhiteSpace(file.DisplayName))
        {
            throw new InvalidDataException($"'{path}' is not a valid device identity.");
        }
        if (!string.Equals(file.DisplayName, thisComputer, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeOperationException(
                $"'{path}' was made for {file.DisplayName}, but this is {thisComputer}. If this profile was copied from another PC, "
                + "delete that file so this PC gets its own identity. If this PC or user was renamed, first finish any session it "
                + $"owns, then change displayName in the file to \"{thisComputer}\".");
        }
        return new DeviceIdentity(file.DeviceId, file.DisplayName);
    }

    public SessionSecret SessionSecret(string session, DateTimeOffset now)
    {
        if (!CoordinationStore.IsValidId(session)) throw new ArgumentException("Invalid session ID.", nameof(session));
        string path = Path.Combine(SessionsDirectory, session + ".json");
        if (File.Exists(path)) return Json.ReadFile<SessionSecret>(path, "Session login");
        Directory.CreateDirectory(SessionsDirectory);
        var secret = new SessionSecret(1, session, "medal-sync", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)), now);
        AtomicFile.CreateNew(path, Json.Serialize(secret));
        return secret;
    }

    public void ForgetSession(string session)
    {
        if (!CoordinationStore.IsValidId(session)) return;
        string path = Path.Combine(SessionsDirectory, session + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}

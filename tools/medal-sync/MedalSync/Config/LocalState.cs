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
    /// Published sessions whose cache or login is still being removed. Outside <c>sessions</c>, so setup,
    /// the configuration wizard and every updater version do not mistake them for unfinished sessions.
    /// </summary>
    private string FinishedDirectory => Path.Combine(Root, "finished-sessions");

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
        if (File.Exists(path)) return ReadLogin(path, session);
        string retained = Path.Combine(Root, "aborted-starts", session, "login.json");
        // A crash between retaining evidence and releasing STARTING must reuse the original control login.
        if (File.Exists(retained)) return ReadLogin(retained, session);
        Directory.CreateDirectory(SessionsDirectory);
        var secret = new SessionSecret(1, session, "medal-sync", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)), now);
        AtomicFile.CreateNew(path, Json.Serialize(secret));
        return secret;
    }

    /// <summary>
    /// Only after the session's generation is committed and its drive stopped: records that, then moves the login out of
    /// <c>sessions</c>. Safe to repeat, including after a crash between the two steps.
    /// </summary>
    internal string RetireSession(string session, string libraryId, string cache, long generation, DateTimeOffset now)
    {
        if (!CoordinationStore.IsValidId(session) || !CoordinationStore.IsValidId(libraryId) || generation < 0)
            throw new InvalidDataException("Invalid finished-session identity.");
        string folder = Path.Combine(FinishedDirectory, session);
        RequireOrdinary(folder);
        string source = Path.Combine(SessionsDirectory, session + ".json");
        RequireOrdinary(source);
        Directory.CreateDirectory(folder);
        string receipt = Path.Combine(folder, FinishedReceiptName);
        RequireOrdinary(receipt);
        if (File.Exists(receipt))
        {
            var saved = ReadFinished(session);
            if (saved.LibraryId != libraryId || saved.Cache != cache || saved.Generation != generation)
                throw new InvalidDataException("The finished-session receipt differs from this session; keep it and investigate.");
        }
        else AtomicFile.CreateNew(receipt, Json.Serialize(new FinishedSession(1, libraryId, session, cache, generation, now,
            $"Uploads drained and verified, and generation {generation} committed, before cleanup. The cache holds nothing the server lacks.")));
        string target = Path.Combine(folder, "login.json");
        RequireOrdinary(target);
        if (File.Exists(source))
        {
            _ = ReadLogin(source, session);
            File.Move(source, target, overwrite: false);
        }
        return folder;
    }

    internal const string FinishedReceiptName = "finished.json";

    /// <summary>Sessions with a finished-session folder, named by valid session IDs only.</summary>
    internal IReadOnlyList<string> FinishedSessionIds() =>
        Directory.Exists(FinishedDirectory)
            ? Directory.EnumerateDirectories(FinishedDirectory).Select(Path.GetFileName).OfType<string>()
                .Where(CoordinationStore.IsValidId).Order(StringComparer.Ordinal).ToArray()
            : [];

    internal FinishedSession ReadFinished(string session)
    {
        if (!CoordinationStore.IsValidId(session)) throw new InvalidDataException("Invalid finished-session identity.");
        string receipt = Path.Combine(FinishedDirectory, session, FinishedReceiptName);
        RequireOrdinary(receipt);
        var saved = Json.ReadFile<FinishedSession>(receipt, "Finished-session receipt");
        if (saved.Format != 1 || saved.Session != session || !CoordinationStore.IsValidId(saved.LibraryId)
            || string.IsNullOrEmpty(saved.Cache) || saved.Generation < 0)
            throw new InvalidDataException("The finished-session receipt is invalid; it was kept.");
        return saved;
    }

    /// <summary>Removes a finished session's folder once its cache is gone: only its receipt and login.</summary>
    internal void ForgetFinished(string session)
    {
        if (!CoordinationStore.IsValidId(session)) return;
        string folder = Path.Combine(FinishedDirectory, session);
        RequireOrdinary(folder);
        if (!Directory.Exists(folder)) return;
        string login = Path.Combine(folder, "login.json");
        if (File.Exists(login)) File.Delete(login);
        string receipt = Path.Combine(folder, FinishedReceiptName);
        if (File.Exists(receipt)) File.Delete(receipt);
        Directory.Delete(folder); // Not recursive: anything else here is not ours, so it is kept.
    }

    /// <summary>Only after a verified stop; the cache is deliberately left at its original location.</summary>
    internal string RetainAbortedStart(string session, string libraryId, string cache, DateTimeOffset now, bool legacyEmptyCache = false)
    {
        if (!CoordinationStore.IsValidId(session) || !CoordinationStore.IsValidId(libraryId))
            throw new InvalidDataException("Invalid aborted-start identity.");
        string folder = Path.Combine(Root, "aborted-starts", session);
        RequireOrdinary(folder);
        string source = Path.Combine(SessionsDirectory, session + ".json");
        RequireOrdinary(source);
        Directory.CreateDirectory(folder);
        string receipt = Path.Combine(folder, "recovery.json");
        RequireOrdinary(receipt);
        if (File.Exists(receipt))
        {
            var saved = Json.ReadFile<AbortedStartEvidence>(receipt, "Aborted-start recovery evidence");
            if (saved.Format != 1 || saved.Session != session || saved.LibraryId != libraryId || saved.Cache != cache)
                throw new InvalidDataException("Aborted-start recovery evidence differs from this session; keep it and investigate.");
        }
        else AtomicFile.CreateNew(receipt, Json.Serialize(new AbortedStartEvidence(1, libraryId, session, cache, now,
            legacyEmptyCache ? "Stopped legacy orphan with absent or empty cache. No upload-success claim."
                : "Mount verified stopped before Medal launch. Cache retained without an upload-success claim.")));
        string target = Path.Combine(folder, "login.json");
        RequireOrdinary(target);
        if (File.Exists(source))
        {
            _ = ReadLogin(source, session);
            File.Move(source, target, overwrite: false);
        }
        return folder;
    }

    internal sealed record FinishedSession(int Format, string LibraryId, string Session, string Cache, long Generation,
        DateTimeOffset FinishedAt, string Detail);

    private sealed record AbortedStartEvidence(int Format, string LibraryId, string Session, string Cache, DateTimeOffset StoppedAt, string Detail);

    private static SessionSecret ReadLogin(string path, string session)
    {
        RequireOrdinary(path);
        var login = Json.ReadFile<SessionSecret>(path, "Session login");
        if (login.Format != 1 || login.Session != session || string.IsNullOrEmpty(login.User) || string.IsNullOrEmpty(login.Password))
            throw new InvalidDataException("The retained session login is invalid; recovery was refused.");
        return login;
    }

    private static void RequireOrdinary(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Aborted-start evidence contains a reparse point; recovery was refused.");
    }
}

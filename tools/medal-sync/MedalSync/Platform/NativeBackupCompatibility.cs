using System.Runtime.Versioning;
using LarsonServer.MedalSync.Config;
using LarsonServer.MedalSync.Library;
using LarsonServer.MedalSync.Util;
using Microsoft.Win32;

namespace LarsonServer.MedalSync.Platform;

[SupportedOSPlatform("windows")]
internal static class NativeBackupCompatibility
{
    public static void Prepare(MedalSyncConfig config, MedalInstall install, LocalState state)
    {
        if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Quit Medal before preparing its native backup identity.");
        // Verified against the installed worker's restore/track implementation;
        // do not extrapolate this compatibility behavior to unknown builds.
        if (install.Versions.App != "2643.583.1" || install.Versions.Recorder != "2643.3040.1")
            throw new IncompatibleLibraryException("Medal's native backup compatibility needs verification for this application/recorder build.");
        string backup = config.ServerCapture.Append("Databases", $"medal-{config.AccountId}.db").Display;
        NativeBackupIdentity.Align(install.DatabasePath, backup, Path.Combine(state.BackupDirectory, "native-identity"), config.AccountId);
        // Medal consults this registry map before it reads ClipFolder from the
        // local DB. A stale old-folder map could first restore another backup.
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Medal\ClipFolders");
        object? original = key.GetValue(config.AccountId);
        if (original is string path && WindowsPath.TryParse(path, out var parsed, out _) && parsed.Equals(config.Capture)) return;
        string saved = Path.Combine(state.Root, "native-backup-originals.json");
        if (!File.Exists(saved)) AtomicFile.CreateNew(saved, Json.Serialize(new { format = 1, accountId = config.AccountId, original }));
        key.SetValue(config.AccountId, config.Capture.Display, RegistryValueKind.String);
        if (key.GetValue(config.AccountId) as string != config.Capture.Display)
            throw new UnsafeOperationException("Medal capture-folder registry readback failed.");
    }
}

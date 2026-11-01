using System.Globalization;
using System.Runtime.Versioning;
using MedalSync.Util;
using Microsoft.Win32;

namespace MedalSync.Platform;

/// <summary>Medal's startup entries before Medal Sync changed them. Same format as set-medal-startup.ps1.</summary>
internal sealed record StartupOriginals(int Format, DateTimeOffset SavedAt, string MedalExecutable,
    IReadOnlyList<SavedRunValue> RunValues, string? Protocol, IReadOnlyList<SavedShortcut> Shortcuts);

internal sealed record SavedRunValue(string Name, string Command);

internal sealed record SavedShortcut(string Path, string Backup);

/// <summary>Shortcuts the portable helper created; removed again on restore.</summary>
internal sealed record LibraryShortcutReceipt(int Format, IReadOnlyList<string> Created);

/// <summary>
/// Turns starting Medal through Medal Sync on or off, the same change as the portable
/// <c>set-medal-startup.ps1</c>, for installed copies (the <c>startup</c> command and the
/// status tray). Turning it on saves Medal's originals once (<c>startup-originals.json</c>
/// plus shortcut copies), which also opts in to the launcher's ongoing repair. Turning it
/// off puts them back and retires that file. Per-user only. Windows' own Startup apps
/// switch is reported, never changed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class StartupIntegration(string stateRoot, string launcher, RegistryKey software,
    IReadOnlyList<string> shortcutFolders, string defaultMedal, Func<DateTimeOffset> now) : IDisposable
{
    public const string LibraryShortcutsFile = "library-shortcuts.json";
    private const string LibraryShortcutName = "Medal Library.lnk";

    public static StartupIntegration ForCurrentUser(string stateRoot, string launcher) => new(stateRoot, launcher,
        Registry.CurrentUser.CreateSubKey("Software"), StartupEntries.ShortcutFolders(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Medal", "current", "Medal.exe"),
        () => DateTimeOffset.UtcNow);

    private string OriginalsPath => Path.Combine(stateRoot, StartupEntries.OriginalsFile);

    /// <summary>Whether Medal starts through Medal Sync (the originals are saved).</summary>
    public bool Enabled => File.Exists(OriginalsPath);

    /// <summary>Windows' Startup apps switch for the Run value: an odd first byte means turned off.</summary>
    public bool TurnedOffInWindows
    {
        get
        {
            using var approved = software.OpenSubKey(StartupEntries.StartupApprovedSubkey);
            return approved?.GetValue(StartupEntries.RunValue) is byte[] { Length: > 0 } value && (value[0] & 1) == 1;
        }
    }

    public const string TurnedOffMessage =
        "Windows has Medal Sync turned off in Settings > Apps > Startup, so it will not start at sign-in until you turn it on there.";

    /// <summary>What turning it on would change. Writes nothing.</summary>
    public IReadOnlyList<string> Preview()
    {
        var lines = new List<string>();
        if (Enabled) lines.Add($"Medal already starts through Medal Sync. Medal's originals are saved in '{OriginalsPath}'.");
        foreach (var value in DirectRunValues()) lines.Add($"Remove the Run value '{value.Name}', which starts Medal without its clip drive (saved first).");
        using (var run = software.OpenSubKey(StartupEntries.RunSubkey))
        {
            string? current = run?.GetValue(StartupEntries.RunValue) as string;
            if (StartupEntries.RunTarget(current) is not { } target || !SamePath(target, launcher))
            {
                lines.Add($"Set the Run value '{StartupEntries.RunValue}' to {StartupEntries.RunCommand(launcher)}");
            }
        }
        string? handler = Protocol();
        if (StartupEntries.StartsMedalDirectly(handler) || StartupEntries.StartsOtherLauncher(handler, launcher))
        {
            lines.Add("Point the medal:// link handler at the launcher.");
        }
        foreach (string shortcut in Shortcuts())
        {
            string target = Target(shortcut);
            if (StartupEntries.StartsMedalDirectly(target) || StartupEntries.StartsOtherLauncher(target, launcher))
            {
                lines.Add($"Point the shortcut '{shortcut}' at the launcher, keeping Medal's icon.");
            }
        }
        if (TurnedOffInWindows) lines.Add(TurnedOffMessage);
        return lines;
    }

    /// <summary>Saves the originals once, then points every entry at the launcher. Returns what changed.</summary>
    public IReadOnlyList<string> Apply()
    {
        if (!File.Exists(launcher)) throw new MedalSyncException($"The Medal Sync launcher is not installed at '{launcher}'. Repair the installation first.");
        if (!File.Exists(Path.Combine(stateRoot, "config.json")))
        {
            throw new MedalSyncException("Configure Medal Sync before starting Medal through it at sign-in. Until then the launcher cannot start Medal.");
        }
        using var _ = StartupEntries.Lock();
        var done = new List<string>();
        if (!Enabled) done.Add(SaveOriginals());
        done.AddRange(StartupEntries.Enforce(stateRoot, launcher, software, Shortcuts()));
        if (TurnedOffInWindows) done.Add(TurnedOffMessage);
        return done;
    }

    /// <summary>Puts Medal's saved entries back and retires the originals file. Returns what changed.</summary>
    public IReadOnlyList<string> Restore()
    {
        // Under the same lock as the supervisor's repair, and with the originals file retired
        // first, so a repair that runs during or after this sees the integration as off.
        using var _ = StartupEntries.Lock();
        if (!Enabled) throw new MedalSyncException("Medal does not start through Medal Sync, so there is nothing to put back.");
        var saved = Json.ReadFile<StartupOriginals>(OriginalsPath, "saved Medal startup entries");
        if (saved.Format != 1) throw new InvalidDataException($"Unsupported format in '{OriginalsPath}'. Nothing was changed.");
        string stamp = now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string retired = $"{OriginalsPath}.restored-{stamp}";
        File.Move(OriginalsPath, retired);
        try
        {
            var done = PutBack(saved, stamp);
            done.Add($"kept the saved originals as '{retired}'");
            return done;
        }
        catch
        {
            // Stay opted in so the entries can be restored again; nothing is lost.
            File.Move(retired, OriginalsPath);
            throw;
        }
    }

    private List<string> PutBack(StartupOriginals saved, string stamp)
    {
        var done = new List<string>();
        using (var run = software.CreateSubKey(StartupEntries.RunSubkey))
        {
            foreach (string name in run.GetValueNames())
            {
                if (StartupEntries.StartsLauncher(run.GetValue(name) as string))
                {
                    run.DeleteValue(name, throwOnMissingValue: false);
                    done.Add($"removed the Run value '{name}'");
                }
            }
            foreach (var value in saved.RunValues)
            {
                run.SetValue(value.Name, value.Command);
                done.Add($"put back Medal's Run value '{value.Name}'");
            }
        }
        if (saved.Protocol is not null)
        {
            using var protocol = software.OpenSubKey(StartupEntries.ProtocolSubkey, writable: true);
            // Leave a handler Medal has since rewritten for itself.
            if (protocol is not null && StartupEntries.StartsLauncher(protocol.GetValue(null) as string))
            {
                protocol.SetValue(null, saved.Protocol);
                done.Add("put back Medal's medal:// link handler");
            }
        }
        foreach (var shortcut in saved.Shortcuts)
        {
            if (!File.Exists(shortcut.Backup)) { done.Add($"kept '{shortcut.Path}': its saved copy '{shortcut.Backup}' is missing"); continue; }
            File.Copy(shortcut.Backup, shortcut.Path, overwrite: true);
            done.Add($"put back the shortcut '{shortcut.Path}'");
        }
        done.AddRange(RemoveLibraryShortcuts(stamp));
        // Shortcuts a Medal update created after turning it on, then repointed by the launcher.
        foreach (string shortcut in Shortcuts())
        {
            if (WasMedalShortcut(shortcut))
            {
                StartupEntries.RetargetShortcut(shortcut, saved.MedalExecutable);
                done.Add($"pointed the shortcut '{shortcut}' back at Medal");
            }
        }
        return done;
    }

    public void Dispose() => software.Dispose();

    private string SaveOriginals()
    {
        var direct = DirectRunValues();
        var shortcuts = Shortcuts().Where(s => StartupEntries.StartsMedalDirectly(Target(s))).ToList();
        string medal = shortcuts.Select(Target).Concat(direct.Select(v => MedalIn(v.Command)))
            .FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? defaultMedal;
        string backup = Path.Combine(stateRoot, "backups", "startup-" + now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backup);
        var saved = new List<SavedShortcut>();
        foreach (string shortcut in shortcuts)
        {
            string copy = Path.Combine(backup, $"{saved.Count + 1}-{Path.GetFileName(shortcut)}");
            File.Copy(shortcut, copy);
            saved.Add(new SavedShortcut(shortcut, copy));
        }
        var originals = new StartupOriginals(1, now(), medal, [.. direct.Select(v => new SavedRunValue(v.Name, v.Command))], Protocol(), saved);
        AtomicFile.CreateNew(OriginalsPath, Json.Serialize(originals));
        return $"saved Medal's original startup entries to '{OriginalsPath}' and shortcut copies to '{backup}'";
    }

    /// <summary>Removes only "Medal Library" shortcuts the portable helper recorded creating, while they still start the launcher.</summary>
    private List<string> RemoveLibraryShortcuts(string stamp)
    {
        var done = new List<string>();
        string receiptPath = Path.Combine(stateRoot, LibraryShortcutsFile);
        if (!File.Exists(receiptPath)) return done;
        var receipt = Json.ReadFile<LibraryShortcutReceipt>(receiptPath, "Medal Library shortcut record");
        if (receipt.Format != 1) throw new InvalidDataException($"Unsupported format in '{receiptPath}'. Nothing more was changed.");
        var allowed = shortcutFolders.Select(f => Path.Combine(f, LibraryShortcutName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in receipt.Created)
        {
            if (!allowed.Contains(path) || !File.Exists(path) || !StartupEntries.StartsLauncher(Target(path))) continue;
            File.Delete(path);
            done.Add($"removed the shortcut '{path}' that turning it on created");
        }
        File.Move(receiptPath, $"{receiptPath}.restored-{stamp}");
        return done;
    }

    private List<SavedRunValue> DirectRunValues()
    {
        using var run = software.OpenSubKey(StartupEntries.RunSubkey);
        return [.. (run?.GetValueNames() ?? [])
            .Select(name => new SavedRunValue(name, run!.GetValue(name) as string ?? ""))
            .Where(v => StartupEntries.StartsMedalDirectly(v.Command))];
    }

    private string? Protocol()
    {
        using var protocol = software.OpenSubKey(StartupEntries.ProtocolSubkey);
        return protocol?.GetValue(null) as string;
    }

    private IEnumerable<string> Shortcuts() => StartupEntries.Shortcuts(shortcutFolders);

    /// <summary>
    /// A Medal shortcut the launcher repointed: it starts the launcher but kept Medal's
    /// icon. Medal Sync's own shortcuts ("Medal Sync", "Medal Library") are never
    /// sent to Medal, so restoring cannot make them bypass the storage checks.
    /// </summary>
    internal static bool WasMedalShortcut(string shortcut)
    {
        string name = Path.GetFileName(shortcut);
        if (name.StartsWith("Medal Sync", StringComparison.OrdinalIgnoreCase)
            || name.Equals(LibraryShortcutName, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return StartupEntries.StartsLauncher(StartupEntries.ShortcutTarget(shortcut))
                && StartupEntries.StartsMedalDirectly(StartupEntries.ShortcutIcon(shortcut));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { return false; }
    }

    private static string Target(string shortcut)
    {
        try { return StartupEntries.ShortcutTarget(shortcut); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { return ""; }
    }

    /// <summary>The quoted Medal.exe path inside a Run command, as the portable helper reads it.</summary>
    internal static string? MedalIn(string command) =>
        command.Split('"').FirstOrDefault(part => StartupEntries.StartsMedalDirectly(part) && Path.IsPathFullyQualified(part.Trim()))?.Trim();

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}

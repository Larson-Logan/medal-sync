using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LarsonServer.MedalSync.Platform;

/// <summary>A place Windows can start Medal from, and whether it goes through the launcher.</summary>
internal sealed record StartupEntry(string Name, string? Command, bool ViaLauncher);

/// <summary>
/// Medal's startup entries: the per-user Run values, the <c>medal://</c> handler
/// and its shortcuts. <c>set-medal-startup.ps1 -Apply</c> points them at the
/// launcher and saves the originals; afterwards the launcher repairs entries
/// Medal recreates, including shortcuts rewritten during a normal start.
/// </summary>
internal static class StartupEntries
{
    public const string LauncherFile = "medal-sync-launch.exe";
    public const string SignInFlag = "--at-sign-in";
    public const string RunValue = "MedalSync";
    public const string OriginalsFile = "startup-originals.json";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ProtocolKey = @"Software\Classes\medal\shell\open\command";

    public static bool StartsLauncher(string? command) =>
        command is not null && command.Contains(LauncherFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>A command that runs Medal's own executable, bypassing the launcher.</summary>
    public static bool StartsMedalDirectly(string? command) =>
        command is not null && !StartsLauncher(command) && command.Contains(@"\Medal.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shortcut files store their target as text (ANSI and UTF-16), so finding the
    /// launcher's file name in the bytes is enough to tell where a shortcut points.
    /// </summary>
    public static bool ShortcutStartsLauncher(ReadOnlySpan<byte> shortcut) =>
        Contains(shortcut, Encoding.ASCII.GetBytes(LauncherFile)) || Contains(shortcut, Encoding.Unicode.GetBytes(LauncherFile));

    public static string RunCommand(string launcher) => $"\"{launcher}\" {SignInFlag}";

    public static string ProtocolCommand(string launcher) => $"\"{launcher}\" \"%1\"";

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length && match; j++)
            {
                match = char.ToLowerInvariant((char)haystack[i + j]) == char.ToLowerInvariant((char)needle[j]);
            }
            if (match) return true;
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<StartupEntry> Read()
    {
        var entries = new List<StartupEntry>();
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            foreach (string name in run?.GetValueNames() ?? [])
            {
                if (run!.GetValue(name) is string value && (StartsLauncher(value) || value.Contains("Medal", StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(new StartupEntry($"HKCU Run: {name}", value, StartsLauncher(value)));
                }
            }
        }
        using (var protocol = Registry.CurrentUser.OpenSubKey(ProtocolKey))
        {
            if (protocol?.GetValue(null) is string handler)
            {
                entries.Add(new StartupEntry("medal:// handler", handler, StartsLauncher(handler)));
            }
        }
        foreach (string shortcut in Shortcuts())
        {
            bool launcher;
            try
            {
                launcher = ShortcutStartsLauncher(File.ReadAllBytes(shortcut));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                launcher = false;
            }
            entries.Add(new StartupEntry($"Shortcut: {shortcut}", null, launcher));
        }
        return entries;
    }

    /// <summary>Medal shortcuts in Startup, Start menu, Desktop and the pinned taskbar.</summary>
    [SupportedOSPlatform("windows")]
    public static IEnumerable<string> Shortcuts()
    {
        string pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        var folders = new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory }
            .Select(Environment.GetFolderPath)
            .Append(pinned);
        foreach (string folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string shortcut in Directory.EnumerateFiles(folder, "*Medal*.lnk", SearchOption.TopDirectoryOnly)) yield return shortcut;
        }
    }

    /// <summary>
    /// After the owner has rewired startup (the originals file exists), keeps the
    /// Run value and the <c>medal://</c> handler pointing at the launcher: removes
    /// Run values that start Medal directly and repoints the handler and
    /// shortcuts. Returns what it did.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> Enforce(string stateRoot, string launcher)
    {
        var done = new List<string>();
        if (!File.Exists(Path.Combine(stateRoot, OriginalsFile)) || !File.Exists(launcher)) return done;
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            foreach (string name in run.GetValueNames())
            {
                if (StartsMedalDirectly(run.GetValue(name) as string))
                {
                    run.DeleteValue(name, throwOnMissingValue: false);
                    done.Add($"removed the Run value '{name}', which started Medal without its clip drive");
                }
            }
            if (!StartsLauncher(run.GetValue(RunValue) as string))
            {
                run.SetValue(RunValue, RunCommand(launcher));
                done.Add($"restored the Run value '{RunValue}'");
            }
        }
        using (var protocol = Registry.CurrentUser.OpenSubKey(ProtocolKey, writable: true))
        {
            if (protocol is not null && StartsMedalDirectly(protocol.GetValue(null) as string))
            {
                protocol.SetValue(null, ProtocolCommand(launcher));
                done.Add("pointed the medal:// handler back at the launcher");
            }
        }
        foreach (string shortcut in Shortcuts())
        {
            if (RepairShortcut(shortcut, launcher)) done.Add($"pointed the shortcut '{shortcut}' back at the launcher");
        }
        return done;
    }

    /// <summary>Repairs only a shortcut targeting Medal.exe, retaining its icon.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool RepairShortcut(string path, string launcher)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            dynamic link = shortcut;
            if (!StartsMedalDirectly((string)link.TargetPath)) return false;
            string icon = link.IconLocation;
            link.TargetPath = launcher;
            link.Arguments = "";
            link.WorkingDirectory = Path.GetDirectoryName(launcher)!;
            if (!string.IsNullOrEmpty(icon)) link.IconLocation = icon;
            link.Save();
            return true;
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}

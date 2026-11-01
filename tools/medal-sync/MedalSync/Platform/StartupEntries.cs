using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;
using MedalSync.Config;
using Microsoft.Win32;

namespace MedalSync.Platform;

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
    internal const string RunSubkey = @"Microsoft\Windows\CurrentVersion\Run";
    internal const string ProtocolSubkey = @"Classes\medal\shell\open\command";
    internal const string StartupApprovedSubkey = @"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string RunKey = @"Software\" + RunSubkey;
    private const string ProtocolKey = @"Software\" + ProtocolSubkey;

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

    /// <summary>
    /// Keeps a previously opted-in integration on the right launcher. Runs from the
    /// supervisor and whenever the launcher starts, so an install or update takes
    /// over startup at once instead of after the next supervised session.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> EnforceExisting(string stateRoot, string executableDirectory)
    {
        // Native setup alone never opts a user into rewriting Medal's hooks.
        if (!File.Exists(Path.Combine(stateRoot, OriginalsFile))) return [];
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        string? launcher = GuardLauncher(executableDirectory,
            LocalState.IsManagedInstall(executableDirectory), run?.GetValue(RunValue) as string);
        return launcher is null ? [] : Enforce(stateRoot, launcher);
    }

    /// <summary>
    /// The launcher startup should use. A setup-managed installation always owns
    /// startup, so a superseded portable copy (such as an old <c>.medal-sync\bin</c>)
    /// is never started again after an upgrade. A portable copy keeps the user's
    /// existing target, including a native one.
    /// </summary>
    internal static string? GuardLauncher(string executableDirectory, bool managed, string? registeredCommand)
    {
        string own = Path.Combine(executableDirectory, LauncherFile);
        if (managed) return File.Exists(own) ? own : null;
        if (RunTarget(registeredCommand) is { } target && File.Exists(target)) return target;
        return File.Exists(own) ? own : null;
    }

    /// <summary>The launcher in our exact Run syntax, or null. Registry text is parsed, never executed.</summary>
    internal static string? RunTarget(string? command)
    {
        if (command is null || !command.StartsWith('"')) return null;
        int end = command.IndexOf('"', 1);
        if (end <= 1 || command[(end + 1)..] != " " + SignInFlag) return null;
        string target = command[1..end];
        return Path.IsPathFullyQualified(target) && Path.GetFileName(target).Equals(LauncherFile, StringComparison.OrdinalIgnoreCase)
            ? target : null;
    }

    /// <summary>A command or shortcut target that starts a launcher other than <paramref name="launcher"/>.</summary>
    internal static bool StartsOtherLauncher(string? command, string launcher)
    {
        if (!StartsLauncher(command)) return false;
        string text = command!.Trim();
        int end = text.StartsWith('"') ? text.IndexOf('"', 1) : -1;
        string executable = end > 1 ? text[1..end] : text;
        return !SamePath(executable, launcher);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

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
    public static IEnumerable<string> Shortcuts() => Shortcuts(ShortcutFolders());

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> ShortcutFolders()
    {
        string pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        return [.. new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory }
            .Select(Environment.GetFolderPath)
            .Append(pinned)];
    }

    public static IEnumerable<string> Shortcuts(IEnumerable<string> folders)
    {
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
    /// shortcuts, including ones left on a superseded launcher. Returns what it did.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> Enforce(string stateRoot, string launcher)
    {
        using var software = Registry.CurrentUser.CreateSubKey("Software");
        return Enforce(stateRoot, launcher, software, Shortcuts());
    }

    /// <summary>As <see cref="Enforce(string, string)"/>, under <paramref name="software"/> (HKCU\Software, or a test key).</summary>
    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<string> Enforce(string stateRoot, string launcher, RegistryKey software, IEnumerable<string> shortcuts)
    {
        var done = new List<string>();
        if (!File.Exists(Path.Combine(stateRoot, OriginalsFile)) || !File.Exists(launcher)) return done;
        using (var run = software.CreateSubKey(RunSubkey))
        {
            foreach (string name in run.GetValueNames())
            {
                if (StartsMedalDirectly(run.GetValue(name) as string))
                {
                    run.DeleteValue(name, throwOnMissingValue: false);
                    done.Add($"removed the Run value '{name}', which started Medal without its clip drive");
                }
            }
            string? current = run.GetValue(RunValue) as string;
            if (RunTarget(current) is not { } registered || !SamePath(registered, launcher))
            {
                run.SetValue(RunValue, RunCommand(launcher));
                done.Add(StartsLauncher(current)
                    ? $"pointed the Run value '{RunValue}' at '{launcher}'"
                    : $"restored the Run value '{RunValue}'");
            }
        }
        using (var protocol = software.OpenSubKey(ProtocolSubkey, writable: true))
        {
            string? handler = protocol?.GetValue(null) as string;
            if (protocol is not null && (StartsMedalDirectly(handler) || StartsOtherLauncher(handler, launcher)))
            {
                protocol.SetValue(null, ProtocolCommand(launcher));
                done.Add("pointed the medal:// handler back at the launcher");
            }
        }
        foreach (string shortcut in shortcuts)
        {
            if (RepairShortcut(shortcut, launcher)) done.Add($"pointed the shortcut '{shortcut}' back at the launcher");
        }
        return done;
    }

    /// <summary>The shortcut's target path, read with WScript.</summary>
    [SupportedOSPlatform("windows")]
    internal static string ShortcutTarget(string path) => WithShortcut(path, link => (string)link.TargetPath);

    /// <summary>The shortcut's icon location (path,index), read with WScript.</summary>
    [SupportedOSPlatform("windows")]
    internal static string ShortcutIcon(string path) => WithShortcut(path, link => (string)link.IconLocation);

    /// <summary>Points a shortcut at <paramref name="target"/> with no arguments, keeping its icon.</summary>
    [SupportedOSPlatform("windows")]
    internal static void RetargetShortcut(string path, string target) => WithShortcut(path, link =>
    {
        link.TargetPath = target;
        link.Arguments = "";
        link.WorkingDirectory = Path.GetDirectoryName(target)!;
        link.Save();
        return true;
    });

    [SupportedOSPlatform("windows")]
    private static T WithShortcut<T>(string path, Func<dynamic, T> use)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            return use(shortcut!);
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Repairs a shortcut targeting Medal.exe, retaining its icon, or one left on
    /// another launcher, retaining its arguments.
    /// </summary>
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
            string target = link.TargetPath;
            string icon = link.IconLocation;
            if (StartsMedalDirectly(target))
            {
                // Without an icon path (WScript reports ",0"), Windows uses the
                // target's icon. Keep Medal's icon before changing the target.
                if (string.IsNullOrEmpty(icon) || icon.StartsWith(',')) icon = target + ",0";
                link.Arguments = "";
            }
            else if (StartsOtherLauncher(target, launcher))
            {
                // The old launcher may be removed; do not keep its file as the icon.
                if (icon.StartsWith(target + ",", StringComparison.OrdinalIgnoreCase)) icon = launcher + icon[target.Length..];
            }
            else return false;
            link.TargetPath = launcher;
            link.WorkingDirectory = Path.GetDirectoryName(launcher)!;
            link.IconLocation = icon;
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

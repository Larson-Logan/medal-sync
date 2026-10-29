using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MedalSync.Coordination;
using MedalSync.Session;

namespace MedalSync.Platform;

internal sealed record MedalInstall(string InstallDirectory, string DatabasePath, string AccountId, string Executable, MedalVersions Versions);

/// <summary>Finds the signed-in Medal user's library and Medal's installed versions.</summary>
internal static partial class MedalEnvironment
{
    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Medal");

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Medal");

    public static MedalInstall Locate() => Locate(InstallDirectory, DataDirectory);

    internal static MedalInstall Locate(string installDirectory, string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory)) throw new UnsafeOperationException($"Medal's data folder '{dataDirectory}' does not exist.");
        var databases = Directory.EnumerateFiles(dataDirectory, "medal-*.db")
            .Where(p => AccountDatabase().IsMatch(Path.GetFileName(p)))
            .ToList();
        if (databases.Count != 1)
        {
            throw new UnsafeOperationException(
                $"Expected exactly one signed-in Medal library in '{dataDirectory}', found {databases.Count}. Sign in to Medal with the shared account.");
        }
        string database = databases[0];
        string account = AccountDatabase().Match(Path.GetFileName(database)).Groups[1].Value;
        return new MedalInstall(installDirectory, database, account, Path.Combine(installDirectory, "current", "Medal.exe"), ReadVersions(installDirectory));
    }

    internal static MedalVersions ReadVersions(string installDirectory)
    {
        // The updater replaces these files independently. A malformed app
        // manifest must not hide an otherwise readable recorder version.
        return new MedalVersions(ReadAppVersion(installDirectory), ReadRecorderVersion(installDirectory));
    }

    private static string? ReadAppVersion(string installDirectory)
    {
        try
        {
            string nuspec = Path.Combine(installDirectory, "current", "sq.version");
            if (File.Exists(nuspec))
            {
                return XDocument.Load(nuspec).Descendants().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException) { }
        return null;
    }

    private static string? ReadRecorderVersion(string installDirectory)
    {
        try
        {
            string versionJson = Path.Combine(installDirectory, "version.json");
            if (File.Exists(versionJson))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(versionJson));
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("RecorderHandler2Version", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString()?.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Versions are evidence for the manifest; missing ones are recorded as null.
        }
        return null;
    }

    [GeneratedRegex("^medal-([0-9]+)\\.db$", RegexOptions.IgnoreCase)]
    private static partial Regex AccountDatabase();
}

/// <summary>
/// Medal's processes: anything running from its install folder, including the
/// tray instance, recorder, encoder and updater. Closing the window only hides
/// Medal in the tray, so the window is never taken as proof that it exited.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MedalProcess(MedalInstall install, string? launchLink = null, Action<ProcessStartInfo>? start = null) : IMedalProcess
{
    private static readonly string[] Names = ["Medal", "MedalEncoder", "Update"];

    private readonly object _gate = new();
    private Process? _seen;

    /// <summary>
    /// Called every few seconds while Medal runs. The Medal process found last time is checked first, through
    /// a handle held since it was found (so a reused process ID cannot pass for it); every other process on
    /// the PC is listed only when that one has exited.
    /// </summary>
    public bool IsRunning()
    {
        lock (_gate)
        {
            if (_seen is not null)
            {
                if (StillRunning(_seen)) return true;
                _seen.Dispose();
                _seen = null;
            }
            string folder = Path.GetFullPath(install.InstallDirectory).TrimEnd('\\') + "\\";
            var processes = Process.GetProcesses();
            Process? found = null;
            try
            {
                foreach (var process in processes)
                {
                    if (IsMedal(process, folder))
                    {
                        found = process;
                        break;
                    }
                }
                if (found is null) return false;
                if (Hold(found)) _seen = found;
                return true;
            }
            finally
            {
                // Each Process is finalizable; disposing them all keeps a poll from feeding the finalizer queue.
                foreach (var process in processes)
                {
                    if (!ReferenceEquals(process, _seen)) process.Dispose();
                }
            }
        }
    }

    private static bool IsMedal(Process process, string folder)
    {
        if (!Names.Any(n => process.ProcessName.StartsWith(n, StringComparison.OrdinalIgnoreCase))) return false;
        try
        {
            string? path = process.MainModule?.FileName;
            return path is null ? IsMedalNamed(process) : path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // An unreadable Medal-named process might be Medal; assume it is.
            return IsMedalNamed(process);
        }
    }

    /// <summary>Opens and keeps the process handle. Without one, the next poll lists every process again.</summary>
    private static bool Hold(Process process)
    {
        try
        {
            _ = process.SafeHandle;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool StillRunning(Process process)
    {
        try { return !process.HasExited; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsMedalNamed(Process process) => process.ProcessName.StartsWith("Medal", StringComparison.OrdinalIgnoreCase);

    public void Launch() => Open(launchLink);

    /// <summary>
    /// Starts Medal, optionally with a <c>medal://</c> link. When Medal is already
    /// running, its single instance takes the link and shows its window.
    /// </summary>
    public void Open(string? link)
    {
        if (!File.Exists(install.Executable)) throw new UnsafeOperationException($"Medal is not installed at '{install.Executable}'.");
        // Shell execute keeps Medal from inheriting medal-sync's console handles.
        var request = new ProcessStartInfo(install.Executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(install.Executable)!,
            Arguments = link is null ? "" : $"\"{MedalLink.Require(link)}\"",
        };
        try
        {
            if (start is not null) start(request);
            else { using var _ = Process.Start(request); }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UnsafeOperationException($"Windows could not open Medal at '{install.Executable}': {ex.Message}. Check the Medal installation, then retry through Medal Sync. Your session and cache are retained.");
        }
    }
}

/// <summary>The only argument the launcher passes on to Medal.</summary>
internal static class MedalLink
{
    private const int MaxLength = 2048;

    public static bool IsLink(string value) => value.StartsWith("medal://", StringComparison.OrdinalIgnoreCase);

    public static string Require(string value) =>
        IsLink(value) && value.Length <= MaxLength && !value.EndsWith('\\') && !value.Any(c => char.IsControl(c) || c == '"')
            ? value
            : throw new UnsafeOperationException("Only a medal:// link of up to 2,048 printable characters, without quotes, is passed to Medal.");
}

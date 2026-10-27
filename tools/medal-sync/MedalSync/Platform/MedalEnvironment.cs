using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LarsonServer.MedalSync.Coordination;
using LarsonServer.MedalSync.Session;

namespace LarsonServer.MedalSync.Platform;

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
        string? app = null;
        string? recorder = null;
        try
        {
            string nuspec = Path.Combine(installDirectory, "current", "sq.version");
            if (File.Exists(nuspec))
            {
                app = XDocument.Load(nuspec).Descendants().FirstOrDefault(e => e.Name.LocalName == "version")?.Value;
            }
            string versionJson = Path.Combine(installDirectory, "version.json");
            if (File.Exists(versionJson))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(versionJson));
                if (document.RootElement.TryGetProperty("RecorderHandler2Version", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    recorder = value.GetString();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            // Versions are evidence for the manifest; missing ones are recorded as null.
        }
        return new MedalVersions(app, recorder);
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
internal sealed class MedalProcess(MedalInstall install) : IMedalProcess
{
    private static readonly string[] Names = ["Medal", "MedalEncoder", "Update"];

    public bool IsRunning()
    {
        string folder = Path.GetFullPath(install.InstallDirectory).TrimEnd('\\') + "\\";
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!Names.Any(n => process.ProcessName.StartsWith(n, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is null ? IsMedalNamed(process) : path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // An unreadable Medal-named process might be Medal; assume it is.
                    if (IsMedalNamed(process)) return true;
                }
            }
        }
        return false;
    }

    private static bool IsMedalNamed(Process process) => process.ProcessName.StartsWith("Medal", StringComparison.OrdinalIgnoreCase);

    public void Launch() => Open(null);

    /// <summary>
    /// Starts Medal, optionally with a <c>medal://</c> link. When Medal is already
    /// running, its single instance takes the link and shows its window.
    /// </summary>
    public void Open(string? link)
    {
        if (!File.Exists(install.Executable)) throw new UnsafeOperationException($"Medal is not installed at '{install.Executable}'.");
        // Shell execute keeps Medal from inheriting medal-sync's console handles.
        using var _ = Process.Start(new ProcessStartInfo(install.Executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(install.Executable)!,
            Arguments = link is null ? "" : $"\"{MedalLink.Require(link)}\"",
        });
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

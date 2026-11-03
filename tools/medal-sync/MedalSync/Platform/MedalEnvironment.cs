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
        string database = AccountDatabasePath(dataDirectory);
        string account = AccountDatabase().Match(Path.GetFileName(database)).Groups[1].Value;
        return new MedalInstall(installDirectory, database, account, Path.Combine(installDirectory, "current", "Medal.exe"), ReadVersions(installDirectory));
    }

    /// <summary>The signed-in account ID, read from the library file name only. Opens no database.</summary>
    public static string SignedInAccountId() => SignedInAccountId(DataDirectory);

    internal static string SignedInAccountId(string dataDirectory) =>
        AccountDatabase().Match(Path.GetFileName(AccountDatabasePath(dataDirectory))).Groups[1].Value;

    private static string AccountDatabasePath(string dataDirectory)
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
        return databases[0];
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
/// <param name="capture">Medal's clip folder, which holds its replay buffer; null reports Medal as never recording.</param>
[SupportedOSPlatform("windows")]
internal sealed class MedalProcess(MedalInstall install, string? launchLink = null, Action<ProcessStartInfo>? start = null,
    Util.WindowsPath? capture = null) : IMedalProcess
{
    private static readonly string[] Names = ["Medal", "MedalEncoder", "Update"];

    /// <summary>How long a poll waits for the replay buffer check before reusing the last answer.</summary>
    private static readonly TimeSpan RecordingWait = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private Process? _seen;
    private readonly object _recordingGate = new();
    private Task<bool>? _recordingCheck;
    private bool _recording;

    public bool IsRecording()
    {
        if (capture is null) return false;
        lock (_recordingGate)
        {
            // The buffer is on the clip drive. A slow drive keeps the last answer instead of holding up supervision,
            // and at most one check is ever outstanding.
            _recordingCheck ??= Task.Run(() => MedalRecording.IsRecording(MedalRecording.BufferFolder(capture).Display, DateTime.UtcNow));
            try
            {
                if (!_recordingCheck.Wait(RecordingWait)) return _recording;
                _recording = _recordingCheck.Result;
            }
            catch (AggregateException)
            {
                _recording = false;
            }
            _recordingCheck = null;
            return _recording;
        }
    }

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

/// <summary>
/// Whether Medal is recording a game. Its recorder process runs whenever Medal is open, so the process list cannot
/// tell. While it records, Medal writes replay buffer segments into <c>Temp\RecordingBuffer</c> under its clip
/// folder every few seconds, and it empties that folder when the game closes. Medal set to buffer in memory writes
/// none, so it then reads as open: this can understate recording, never invent it.
/// </summary>
internal static class MedalRecording
{
    /// <summary>A segment written this recently means recording; older leftovers (after a crash) do not.</summary>
    internal static readonly TimeSpan Recent = TimeSpan.FromSeconds(60);

    public static Util.WindowsPath BufferFolder(Util.WindowsPath capture) => capture.Append("Temp", "RecordingBuffer");

    public static bool IsRecording(string bufferFolder, DateTime nowUtc)
    {
        try
        {
            foreach (var file in new DirectoryInfo(bufferFolder).EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                var written = file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc;
                var age = nowUtc - written;
                if (age <= Recent && age >= -Recent) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // No buffer folder (no game recorded yet) or an unreadable one: not known to be recording.
        }
        return false;
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

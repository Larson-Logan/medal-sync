using System.Diagnostics;
using System.Text;

namespace MedalSync.Platform;

/// <summary>Runs one rclone command. Errors are drained, never retained: they can contain credentials or private filenames.</summary>
internal sealed class RcloneRunner(string rclonePath)
{
    /// <summary>Returns standard output when <paramref name="captureOutput"/> is set; throws <see cref="RcloneFailedException"/> otherwise.</summary>
    public string Run(IReadOnlyList<string> arguments, TimeSpan timeout, Action? requireIdle = null, bool captureOutput = false)
    {
        var start = new ProcessStartInfo(rclonePath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Process? child;
        try { child = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception ex) { throw new RcloneFailedException("Cannot start rclone. Check rclonePath and retry.", inner: ex); }
        using var process = child ?? throw new RcloneFailedException("rclone did not start.");
        var output = ReadOutput(process.StandardOutput, captureOutput);
        var errors = ReadOutput(process.StandardError, retain: false);
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (!process.WaitForExit(200))
            {
                if (output.IsFaulted) output.GetAwaiter().GetResult();
                if (errors.IsFaulted) errors.GetAwaiter().GetResult();
                requireIdle?.Invoke();
                if (elapsed.Elapsed > timeout)
                    throw new RcloneFailedException($"rclone {arguments[0]} timed out; ownership and local media are retained. Check timeoutMinutes and retry finish.");
            }
            process.WaitForExit();
            requireIdle?.Invoke();
            string text = output.GetAwaiter().GetResult();
            errors.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new RcloneFailedException($"rclone {arguments[0]} failed (exit {process.ExitCode}); check provider access, quota and conflicting files, then retry finish. Ownership and media are retained.", process.ExitCode);
            return text;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
    }

    private static async Task<string> ReadOutput(TextReader reader, bool retain)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (!retain) continue;
            if (output.Length + count > 16 * 1024 * 1024)
                throw new RcloneFailedException("Primary listing exceeds the supported size; verification was refused.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}

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
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (captureOutput && e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (!process.WaitForExit(200))
            {
                requireIdle?.Invoke();
                if (elapsed.Elapsed > timeout)
                    throw new RcloneFailedException($"rclone {arguments[0]} timed out; ownership and local media are retained. Check timeoutMinutes and retry finish.");
            }
            process.WaitForExit();
            requireIdle?.Invoke();
            if (process.ExitCode != 0)
                throw new RcloneFailedException($"rclone {arguments[0]} failed (exit {process.ExitCode}); check provider access, quota and conflicting files, then retry finish. Ownership and media are retained.", process.ExitCode);
            lock (output) return output.ToString();
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
    }
}

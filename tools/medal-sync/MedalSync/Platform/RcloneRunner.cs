using System.Diagnostics;
using System.Text;

namespace MedalSync.Platform;

/// <summary>Runs one non-interactive rclone command. Errors are drained, never retained: they can contain credentials or private filenames.</summary>
internal sealed class RcloneRunner(string rclonePath)
{
    /// <summary>Returns standard output when <paramref name="captureOutput"/> is set; throws <see cref="RcloneFailedException"/> otherwise.</summary>
    public string Run(IReadOnlyList<string> arguments, TimeSpan timeout, Action? requireIdle = null, bool captureOutput = false)
    {
        var start = new ProcessStartInfo(rclonePath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (argument.Equals("--ask-password", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < arguments.Count && bool.TryParse(arguments[i + 1], out _)) i++;
                continue;
            }
            if (argument.StartsWith("--ask-password=", StringComparison.OrdinalIgnoreCase)) continue;
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("--ask-password=false");
        Process? child;
        try { child = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception ex) { throw new RcloneFailedException("Cannot start rclone. Check rclonePath and retry.", inner: ex); }
        using var process = child ?? throw new RcloneFailedException("rclone did not start.");
        try { process.StandardInput.Close(); }
        catch (IOException) { /* The child may have already closed its input. */ }
        var output = ReadOutput(process.StandardOutput, captureOutput);
        var errors = ScanErrors(process.StandardError);
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (!process.WaitForExit(200))
            {
                if (output.IsFaulted) output.GetAwaiter().GetResult();
                if (errors.IsFaulted) errors.GetAwaiter().GetResult();
                requireIdle?.Invoke();
                if (elapsed.Elapsed > timeout)
                    throw new RcloneFailedException($"rclone {arguments[0]} timed out; ownership and local media are retained. Check timeoutMinutes and retry finish.",
                        timedOut: true, command: arguments[0]);
            }
            process.WaitForExit();
            requireIdle?.Invoke();
            string text = output.GetAwaiter().GetResult();
            var hints = errors.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new RcloneFailedException($"rclone {arguments[0]} failed (exit {process.ExitCode}); check provider access, quota and conflicting files, then retry finish. Ownership and media are retained.",
                    process.ExitCode, hints: hints, command: arguments[0]);
            return text;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
    }

    /// <summary>
    /// Reads rclone's error output to the end and keeps only which kinds of problem it mentions.
    /// A network symptom wins over every other hint, so an outage while refreshing a token still reads as an outage.
    /// </summary>
    private static async Task<RcloneHints> ScanErrors(TextReader reader)
    {
        var hints = RcloneHints.None;
        var buffer = new char[4096];
        string carry = "";
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            // A short overlap catches a phrase split across two reads.
            string text = carry + new string(buffer, 0, count).ToLowerInvariant();
            hints |= Classify(text);
            carry = text.Length > 64 ? text[^64..] : text;
        }
        return hints;
    }

    internal static RcloneHints Classify(string lowercase)
    {
        static bool Any(string text, params string[] phrases) => phrases.Any(p => text.Contains(p, StringComparison.Ordinal));
        var hints = RcloneHints.None;
        if (Any(lowercase, "no such host", "dial tcp", "connection refused", "connection reset", "network is unreachable", "no route to host",
            "i/o timeout", "tls handshake timeout", "context deadline exceeded", "temporary failure in name resolution", "server misbehaving"))
            hints |= RcloneHints.Network;
        if (Any(lowercase, "invalid_grant", "couldn't fetch token", "token expired", "expired token", "unauthorized", "unauthenticated",
            "authentication", "config reconnect", "invalid_client", "access_denied"))
            hints |= RcloneHints.SignIn;
        if (Any(lowercase, "didn't find section in config file", "failed to load config", "couldn't decrypt", "config password",
            "configuration password", "unknown backend", "remote not found"))
            hints |= RcloneHints.Config;
        if (Any(lowercase, "quota", "insufficient_space", "insufficient storage", "insufficient space", "storage is full", "not enough space"))
            hints |= RcloneHints.Quota;
        if (Any(lowercase, "immutable"))
            hints |= RcloneHints.Conflict;
        return hints;
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

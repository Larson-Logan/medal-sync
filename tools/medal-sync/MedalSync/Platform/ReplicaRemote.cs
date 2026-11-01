using System.Globalization;
using System.Text.Json;
using MedalSync.Config;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>One file at a replica destination, relative to the library folder there with '/' separators.</summary>
internal sealed record RemoteFile(string Path, long Size, DateTimeOffset ModTime);

internal enum RemoteCheck { Match, Missing, Differs, Error }

/// <summary>
/// Per-file results of a byte comparison. Files without a result were not reached. <see cref="Failure"/> is set when the
/// comparison stopped before every file had a result (an outage, a sign-in problem, a timeout).
/// </summary>
internal sealed record RemoteCheckOutcome(IReadOnlyDictionary<string, RemoteCheck> Results, RcloneFailedException? Failure);

/// <summary>The few operations the replica audit and cleanup need at a destination. Each one names exact files.</summary>
internal interface IReplicaRemote
{
    /// <summary>Every file under <paramref name="destination"/>, recursively. A destination that does not exist is empty.</summary>
    IReadOnlyList<RemoteFile> List(string destination);

    /// <summary>
    /// Reads back and compares exactly <paramref name="files"/> from the source; never copies or changes anything.
    /// <paramref name="guard"/> runs while the comparison is in progress and stops it by throwing.
    /// </summary>
    RemoteCheckOutcome Check(string source, string destination, IReadOnlyList<string> files, Action? guard = null);

    /// <summary>Moves exactly <paramref name="files"/> under <paramref name="folder"/>, a relative folder inside the destination.</summary>
    void Move(string destination, IReadOnlyList<string> files, string folder);

    /// <summary>Creates the empty file <paramref name="name"/> in <paramref name="folder"/>, a relative folder inside the destination.</summary>
    void Mark(string destination, string folder, string name);

    /// <summary>Deletes <paramref name="folder"/>, a relative folder inside the destination, and everything in it.</summary>
    void Purge(string destination, string folder);
}

/// <summary>Runs the operations with the pinned rclone and the configured credential file. Output is parsed, never stored.</summary>
internal sealed class RcloneReplicaRemote(MedalSyncConfig config, LocalState state, Action requireIdle,
    Func<IReadOnlyList<string>, string>? run = null) : IReplicaRemote
{
    private string Credentials
    {
        get
        {
            string path = Environment.ExpandEnvironmentVariables(config.Replication?.RcloneConfigPath
                ?? throw new UnsafeOperationException("No cloud copies are configured."));
            return File.Exists(path) ? path
                : throw new RcloneFailedException($"The rclone credential file is missing at {path}. Copy it to this PC from the PC where you ran 'rclone config', or create it there again.");
        }
    }

    private TimeSpan Timeout => TimeSpan.FromMinutes(config.Replication?.TimeoutMinutes ?? 240);

    private static readonly string[] Network = ["--retries", "2", "--low-level-retries", "3", "--contimeout", "10s", "--timeout", "60s"];

    public IReadOnlyList<RemoteFile> List(string destination)
    {
        string text;
        try
        {
            // Include directories so ambiguous folder identities and directory links are refused too.
            text = Run(["lsjson", destination, "--config", Credentials, "--recursive", "--no-mimetype", .. Network], capture: true);
        }
        catch (RcloneFailedException ex) when (ex.ExitCode == 3)
        {
            return []; // Directory not found: nothing was ever copied here.
        }
        return Parse(text);
    }

    internal static IReadOnlyList<RemoteFile> Parse(string json)
    {
        const string unreadable = "rclone returned an unreadable or incomplete replica listing. Inspect the remote with 'rclone lsjson', then retry cleanup.";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException(unreadable);
            var files = new List<RemoteFile>();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException(unreadable);
                foreach (string flag in new[] { "IsDir", "IsLink" })
                    if (item.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException(unreadable);
                if (!item.TryGetProperty("Path", out var path) || path.ValueKind != JsonValueKind.String
                    || path.GetString() is not { } relative || !SafeRelativePath(relative)
                    || item.TryGetProperty("IsLink", out var link) && link.ValueKind == JsonValueKind.True)
                    throw new InvalidDataException("rclone returned an unsafe path or link in the replica listing. Correct the remote's names or links, then retry cleanup.");
                if (!identities.Add(relative))
                    throw new InvalidDataException("rclone returned duplicate or case-conflicting replica paths. Review duplicate names with 'rclone dedupe', then retry cleanup.");
                if (item.TryGetProperty("IsDir", out var dir) && dir.ValueKind == JsonValueKind.True) continue;
                if (!item.TryGetProperty("Size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes < 0
                    || !item.TryGetProperty("ModTime", out var time) || time.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var modified))
                    throw new InvalidDataException(unreadable);
                files.Add(new(relative, bytes, modified.ToUniversalTime()));
            }
            return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { throw new InvalidDataException(unreadable); }
    }

    private static bool SafeRelativePath(string path) =>
        !path.Contains('\\') && !path.StartsWith('/') && !path.Split('/').Any(s => s is "" or "." or "..")
        && WindowsPath.TryParse(@"M:\Clips\" + path.Replace('/', '\\'), out _, out _);

    public RemoteCheckOutcome Check(string source, string destination, IReadOnlyList<string> files, Action? guard = null)
    {
        if (files.Count == 0) return new(new Dictionary<string, RemoteCheck>(), null);
        return WithFiles(files, list =>
        {
            string combined = Path.Combine(state.Root, "replica-check-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                List<string> arguments = ["check", source, destination, "--config", Credentials, "--files-from-raw", list,
                    "--one-way", "--download", "--combined", combined, .. Network, "--transfers", "2"];
                if (config.Replication?.BandwidthLimitMiB is { } bandwidth)
                    arguments.AddRange(["--bwlimit", bandwidth.ToString(CultureInfo.InvariantCulture) + "Mi"]);
                RcloneFailedException? failure = null;
                try { Run(arguments, guard: guard); }
                catch (RcloneFailedException ex) { failure = ex; }
                var results = File.Exists(combined) ? ParseCombined(File.ReadAllLines(combined)) : new Dictionary<string, RemoteCheck>(StringComparer.Ordinal);
                // rclone exits 1 when it found differences; that is a finished check only if every file has a result.
                bool complete = files.All(results.ContainsKey);
                if (complete && (failure is null || failure.ExitCode == 1)) return new RemoteCheckOutcome(results, null);
                return new RemoteCheckOutcome(results,
                    failure ?? new RcloneFailedException("rclone check ended without a result for every file.", command: "check"));
            }
            finally { File.Delete(combined); }
        });
    }

    /// <summary>rclone's combined report: '=' identical, '+' missing at the destination, '*' different, '!' error; '-' is ignored.</summary>
    internal static Dictionary<string, RemoteCheck> ParseCombined(IEnumerable<string> lines)
    {
        var results = new Dictionary<string, RemoteCheck>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (line.Length < 3 || line[1] != ' ') continue;
            RemoteCheck? result = line[0] switch { '=' => RemoteCheck.Match, '+' => RemoteCheck.Missing, '*' => RemoteCheck.Differs, '!' => RemoteCheck.Error, _ => null };
            if (result is { } r) results[line[2..]] = r;
        }
        return results;
    }

    public void Move(string destination, IReadOnlyList<string> files, string folder)
    {
        if (files.Count == 0) return;
        RequireRelative(folder);
        WithFiles(files, list =>
        {
            // The trash folder is excluded by the exact file list, so rclone accepts a destination inside the source.
            Run(["move", destination, destination.TrimEnd('/') + "/" + folder, "--config", Credentials, "--files-from-raw", list,
                "--no-traverse", .. Network, "--transfers", "2"]);
            return 0;
        });
    }

    public void Mark(string destination, string folder, string name)
    {
        RequireRelative(folder);
        RequireRelative(name);
        // touch creates the file when it is missing, along with any folder the provider needs.
        Run(["touch", destination.TrimEnd('/') + "/" + folder + "/" + name, "--config", Credentials, .. Network]);
    }

    public void Purge(string destination, string folder)
    {
        RequireRelative(folder);
        Run(["purge", destination.TrimEnd('/') + "/" + folder, "--config", Credentials, .. Network]);
    }

    private static void RequireRelative(string folder)
    {
        if (folder.Length == 0 || folder.StartsWith('/') || folder.Split('/').Any(s => s is "" or "." or ".."))
            throw new ArgumentException("Expected a relative folder inside the destination.", nameof(folder));
    }

    private T WithFiles<T>(IReadOnlyList<string> files, Func<string, T> run)
    {
        Directory.CreateDirectory(state.Root);
        string list = Path.Combine(state.Root, "replica-files-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(list, files, new System.Text.UTF8Encoding(false));
            return run(list);
        }
        finally { File.Delete(list); }
    }

    private string Run(IReadOnlyList<string> arguments, bool capture = false, Action? guard = null)
    {
        void Poll()
        {
            requireIdle();
            guard?.Invoke();
        }
        Poll();
        return run is null ? new RcloneRunner(config.ExpandedRclonePath).Run(arguments, Timeout, Poll, capture) : run(arguments);
    }
}

using System.Diagnostics;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>Required copies finish and pass byte verification before the coordinator releases ownership.</summary>
internal sealed class StorageReplication(MedalSyncConfig config, LocalState state, Action requireIdle,
    Action<IReadOnlyList<string>>? execute = null) : IStorageReplication
{
    private readonly IFileProbe _probe = new FileSystemProbe();

    public void Prepare(LibrarySnapshot snapshot)
    {
        if (config.Replication?.LocalCopyRoot is null) return;
        requireIdle();
        RequireMarker(config.ServerCapture);
        string marker = config.MountedCapture.Append(MedalSyncConfig.StorageMarker).Display;
        // Claim a dedicated folder only; never adopt an unrelated tree or another library.
        if (Directory.Exists(config.MountedCapture.Display) && Directory.EnumerateFileSystemEntries(config.MountedCapture.Display).Any())
            RequireMarker(config.MountedCapture);
        RequireOrdinaryDirectory(config.MountedCapture);
        var files = ReplicationPlan.Files(snapshot, config.Capture, config.ServerCapture, _probe);
        Directory.CreateDirectory(config.MountedCapture.Display);
        if (!File.Exists(marker)) AtomicFile.CreateNew(marker, System.Text.Encoding.UTF8.GetBytes(config.LibraryId + "\n"));
        CopyAndCheck(config.ServerCapture.Display, config.MountedCapture.Display, files, cloud: false);
    }

    public void Publish(LibrarySnapshot snapshot)
    {
        if (config.Replication is not { } replication) return;
        requireIdle();
        RequireMarker(config.ServerCapture);
        var source = config.MountedCapture;
        RequireMarker(source);
        RequireOrdinaryDirectory(source);
        var files = ReplicationPlan.Files(snapshot, config.Capture, source, _probe);
        if (replication.LocalCopyRoot is not null)
            CopyAndCheck(source.Display, config.ServerCapture.Display, files, cloud: false);
        foreach (var target in replication.Targets)
        {
            requireIdle();
            CopyAndCheck(source.Display, ReplicationPlan.Destination(target, config.LibraryId), files, cloud: true);
        }
        requireIdle();
        RequireMarker(source);
        RequireMarker(config.ServerCapture);
        Directory.CreateDirectory(Path.Combine(state.Root, "replication"));
        AtomicFile.Replace(Path.Combine(state.Root, "replication", "last-verified.json"), Json.Serialize(new
        {
            format = 1, config.LibraryId, snapshotDigest = snapshot.Digest(), verifiedAt = DateTimeOffset.UtcNow,
            files = files.Count, localCopy = replication.LocalCopyRoot is not null, targets = replication.Targets.Select(t => t.Name).ToArray(),
        }));
    }

    private void CopyAndCheck(string source, string target, IReadOnlyList<string> files, bool cloud)
    {
        requireIdle();
        if (files.Count == 0) return;
        if (WindowsPath.TryParse(target, out var destination, out _))
        {
            RequireOrdinaryDirectory(destination);
            foreach (string file in files)
            {
                var probe = _probe.Probe(destination.Append(file.Split('/')), destination);
                if (probe.HasReparsePoint || probe.IsDirectory)
                    throw new IOException("A replica destination contains a link or directory in place of media; copying was refused.");
            }
        }
        string configFile = cloud ? Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!)
            : Path.Combine(state.Root, "rclone-empty.conf");
        if (cloud && !File.Exists(configFile)) throw new IOException("The configured rclone credential file is missing; configure the provider before retrying finish.");
        Directory.CreateDirectory(state.Root);
        string list = Path.Combine(state.Root, "replication-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(list, files, new System.Text.UTF8Encoding(false));
            foreach (string command in new[] { "copy", "check" })
            {
                requireIdle();
                var arguments = ReplicationPlan.Arguments(command, source, target, list, configFile);
                if (execute is not null) execute(arguments); else Execute(arguments);
            }
        }
        finally { File.Delete(list); }
    }

    private void Execute(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(config.ExpandedRclonePath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Process? child;
        try { child = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("Cannot start rclone for replication. Check rclonePath and retry finish.", ex); }
        using var process = child ?? throw new IOException("rclone replication did not start.");
        // Drain output without retaining provider errors that could contain credentials or private filenames.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (!process.WaitForExit(200))
            {
                requireIdle();
                if (elapsed.Elapsed > TimeSpan.FromMinutes(config.Replication?.TimeoutMinutes ?? 240))
                    throw new IOException("Replication timed out; ownership and local media are retained. Check timeoutMinutes and retry finish.");
            }
            requireIdle();
            if (process.ExitCode != 0) throw new IOException($"rclone {arguments[0]} failed (exit {process.ExitCode}); check provider access, quota and conflicting files, then retry finish. Ownership and media are retained.");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
    }

    private void RequireMarker(WindowsPath folder)
    {
        RequireOrdinaryDirectory(folder);
        if (File.ReadAllText(folder.Append(MedalSyncConfig.StorageMarker).Display).Trim() != config.LibraryId)
            throw new IOException("Storage belongs to another library; replication was refused.");
    }

    private static void RequireOrdinaryDirectory(WindowsPath folder)
    {
        var current = WindowsPath.Parse(folder.Root);
        foreach (string segment in folder.Segments)
        {
            current = current.Append(segment);
            if (Directory.Exists(current.Display) && File.GetAttributes(current.Display).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Storage contains a reparse point; replication was refused.");
        }
    }
}

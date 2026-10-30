using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Util;

namespace MedalSync.Storage;

internal sealed record VerifiedMedia(string Path, long Bytes, DateTime ModifiedUtc);
internal sealed record DestinationVerification(int Format, string Binding, string SnapshotDigest,
    DateTimeOffset FullVerifiedAt, int PassesSinceFull, IReadOnlyList<VerifiedMedia> Files, string Integrity = "");

/// <summary>Private per-destination evidence. Removing it before a pass makes interruption fall back to a full pass.</summary>
internal sealed class ReplicationVerification(MedalSyncConfig config, LocalState state, string source, string destination,
    string snapshotDigest, IReadOnlyList<string> files, DateTimeOffset now)
{
    private string Binding => Hashing.Sha256Hex(Json.Serialize(new
    {
        config.LibraryId, source, destination, config,
        credentials = config.Replication?.RcloneConfigPath is { } path && File.Exists(Environment.ExpandEnvironmentVariables(path))
            ? Hashing.Sha256File(Environment.ExpandEnvironmentVariables(path)) : null,
    }));

    private string ReceiptPath => Path.Combine(state.Root, "replication", "destinations",
        Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(destination)) + ".json");

    public void Run(Action<IReadOnlyList<string>, bool> transfer, Action<IReadOnlyList<string>> listing,
        Action<int, long, bool> report, Action requireUnchanged)
    {
        string binding = Binding;
        var previous = Read(binding);
        Directory.CreateDirectory(Path.GetDirectoryName(ReceiptPath)!);
        File.Delete(ReceiptPath);
        var before = Identify();
        bool full = previous is null || previous.PassesSinceFull >= config.Replication!.FullVerifyEveryHandoffs - 1
            || now - previous.FullVerifiedAt >= TimeSpan.FromDays(config.Replication!.FullVerifyEveryDays);
        var old = previous?.Files.ToDictionary(f => f.Path, StringComparer.Ordinal) ?? [];
        var delta = before.Where(f => full || !old.TryGetValue(f.Path, out var prior) || prior != f).ToArray();
        var changed = delta.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var unchanged = before.Where(f => !changed.Contains(f.Path)).Select(f => f.Path).ToArray();
        // A failed copy, listing, byte check, cancellation or source recheck leaves no reusable receipt.
        report(delta.Length, delta.Sum(f => f.Bytes), full);
        if (unchanged.Length > 0) listing(unchanged);
        transfer(delta.Select(f => f.Path).ToArray(), full);
        requireUnchanged();
        if (!before.SequenceEqual(Identify()) || binding != Binding)
            throw new IOException("Media or replication configuration changed during verification; retry with Medal quit.");
        var receipt = new DestinationVerification(1, binding, snapshotDigest,
            full ? now : previous!.FullVerifiedAt, full ? 0 : previous!.PassesSinceFull + 1, before);
        AtomicFile.Replace(ReceiptPath, Json.Serialize(receipt with { Integrity = Hashing.Sha256Hex(Json.Serialize(receipt)) }));
    }

    public void Invalidate() => File.Delete(ReceiptPath);

    private IReadOnlyList<VerifiedMedia> Identify() => files.Select(relative =>
    {
        var root = WindowsPath.Parse(source);
        var path = root.Append(relative.Split('/'));
        var found = new FileSystemProbe().Probe(path, root);
        if (!found.Exists || found.HasReparsePoint || found.IsDirectory)
            throw new IOException("Referenced source media is missing or unsafe; verification was refused.");
        return new VerifiedMedia(relative, found.Length, found.LastWriteUtc);
    }).ToArray();

    private DestinationVerification? Read(string binding)
    {
        try
        {
            var receipt = Json.ReadFile<DestinationVerification>(ReceiptPath, "destination verification");
            if (receipt.Integrity != Hashing.Sha256Hex(Json.Serialize(receipt with { Integrity = "" }))) return null;
            if (receipt.Format != 1 || receipt.Binding != binding || receipt.SnapshotDigest is not { Length: 64 }
                || receipt.FullVerifiedAt > now || receipt.FullVerifiedAt == default || receipt.PassesSinceFull < 0
                || receipt.Files is null || receipt.Files.Any(f => f is null || f.Path is null || f.Bytes < 0 || f.ModifiedUtc == default)
                || receipt.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != receipt.Files.Count) return null;
            return receipt;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }
}

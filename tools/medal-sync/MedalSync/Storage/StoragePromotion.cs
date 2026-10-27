using MedalSync.Config;
using MedalSync.Util;

namespace MedalSync.Storage;

internal sealed record PromotionFile(string Relative, long Bytes, string Sha256);
internal sealed record PromotionReceipt(int Format, string Source, string Target, string LibraryId, string SpikeId,
    string Stage, string? PreservedTarget, string PreservedMarker, IReadOnlyList<PromotionFile> Files);

/// <summary>Stopped, same-share directory rename. Every file is hashed; nothing is deleted.</summary>
internal static class StoragePromotion
{
    public static IReadOnlyList<string> Inspect(string source, string target, string receiptDirectory, string spikeId, string libraryId)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        receiptDirectory = Path.GetFullPath(receiptDirectory);
        if (Within(source, target) || Within(target, source) || Within(receiptDirectory, source) || Within(receiptDirectory, target)
            || Within(source, receiptDirectory) || Within(target, receiptDirectory))
            throw new UnsafeOperationException("Source, destination and private receipt directory must not contain one another.");
        RequireNoLinkedAncestors(source);
        RequireNoLinkedAncestors(target);
        RequireNoLinkedAncestors(receiptDirectory);
        string receiptPath = Path.Combine(receiptDirectory, "receipt.json");
        if (File.Exists(receiptPath))
        {
            var receipt = Json.ReadFile<PromotionReceipt>(receiptPath, "Promotion receipt");
            if (receipt.Format != 1 || receipt.Source != source || receipt.Target != target || receipt.LibraryId != libraryId || receipt.SpikeId != spikeId)
                throw new UnsafeOperationException("Existing promotion receipt belongs to a different operation.");
            if (receipt.Stage != "complete") throw new UnsafeOperationException($"Previous promotion stopped at '{receipt.Stage}'. Inspect its receipt and retained folders; do not start another move.");
            if (Directory.Exists(source) || !Directory.Exists(target)) throw new UnsafeOperationException("Completed promotion paths changed; inspect before continuing.");
            string marker = Path.Combine(target, MedalSyncConfig.StorageMarker);
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() != libraryId)
                throw new UnsafeOperationException("Production marker belongs to a different library.");
            return [];
        }
        if (Directory.Exists(receiptDirectory) && Directory.EnumerateFileSystemEntries(receiptDirectory).Any())
            throw new UnsafeOperationException("Receipt directory is not empty.");
        if (!Directory.Exists(source)) throw new UnsafeOperationException("Spike clip directory is missing; never infer an already-completed move without its receipt.");
        RequireNoLinks(source);
        if (Directory.Exists(target))
        {
            RequireNoLinks(target);
            if (Directory.EnumerateFileSystemEntries(target).Any()) throw new UnsafeOperationException("Production destination is not empty; nothing will be merged or replaced.");
        }
        else if (File.Exists(target)) throw new UnsafeOperationException("Production destination is a file.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new UnsafeOperationException("Production parent directory must already exist.");
        if (File.ReadAllText(Path.Combine(source, MedalSyncConfig.StorageMarker)).Trim() != spikeId)
            throw new UnsafeOperationException("Spike storage marker does not match the spike configuration.");
        return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
    }

    public static PromotionReceipt Apply(string source, string target, string receiptDirectory, string spikeId, string libraryId,
        Action<string> saveCheckpoint)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        receiptDirectory = Path.GetFullPath(receiptDirectory);
        var paths = Inspect(source, target, receiptDirectory, spikeId, libraryId);
        string receiptPath = Path.Combine(receiptDirectory, "receipt.json");
        if (paths.Count == 0 && File.Exists(receiptPath)) return Json.ReadFile<PromotionReceipt>(receiptPath, "Promotion receipt");
        var files = paths.Select(p => new PromotionFile(Path.GetRelativePath(source, p), new FileInfo(p).Length, Hashing.Sha256File(p))).ToArray();
        Directory.CreateDirectory(receiptDirectory);
        saveCheckpoint(Path.Combine(receiptDirectory, "before-promotion.json"));
        string? preservedTarget = Directory.Exists(target) ? Path.Combine(Path.GetDirectoryName(source)!, $"destination-before-{libraryId}") : null;
        string preservedMarker = Path.Combine(Path.GetDirectoryName(source)!, $"spike-marker-before-{libraryId}");
        if ((preservedTarget is not null && Path.Exists(preservedTarget)) || Path.Exists(preservedMarker))
            throw new UnsafeOperationException("A preservation path already exists; inspect before continuing.");
        var receipt = new PromotionReceipt(1, source, target, libraryId, spikeId, "prepared", preservedTarget, preservedMarker, files);
        void Save(string stage)
        {
            receipt = receipt with { Stage = stage };
            AtomicFile.Replace(receiptPath, Json.Serialize(receipt));
        }
        Save("prepared");
        if (preservedTarget is not null) Directory.Move(target, preservedTarget);
        Save("destination-preserved");
        Directory.Move(source, target);
        Save("moved");
        var after = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(target, p)).Order(StringComparer.Ordinal).ToArray();
        if (!after.SequenceEqual(files.Select(f => f.Relative).Order(StringComparer.Ordinal))) throw new IOException("Moved inventory differs; all folders and the receipt are retained for inspection.");
        foreach (var file in files)
        {
            string path = Path.Combine(target, file.Relative);
            if (new FileInfo(path).Length != file.Bytes || Hashing.Sha256File(path) != file.Sha256)
                throw new IOException($"Moved file verification failed: {file.Relative}. Do not initialize production; inspect the receipt.");
        }
        Save("verified");
        File.Move(Path.Combine(target, MedalSyncConfig.StorageMarker), preservedMarker);
        Save("complete");
        return receipt;
    }

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RequireNoLinks(string root)
    {
        RequireNoLinkedAncestors(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnsafeOperationException("Spike or destination contains a link.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }

    private static void RequireNoLinkedAncestors(string root)
    {
        for (string? path = root; path is not null; path = Path.GetDirectoryName(path))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnsafeOperationException("Promotion paths must not cross a link.");
        }
    }
}

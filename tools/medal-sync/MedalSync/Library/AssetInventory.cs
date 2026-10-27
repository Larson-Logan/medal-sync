using System.Text.Json;
using MedalSync.Util;

namespace MedalSync.Library;

internal enum AssetRole
{
    /// <summary>A path column Medal opens directly (video, image, thumbnail, film reel).</summary>
    Required,

    /// <summary>A known metadata path (keyboard overlay). Missing is a warning, not a blocker.</summary>
    Optional,

    /// <summary>An absolute path in metadata this adapter does not know. Reported for review.</summary>
    Unrecognized,
}

internal sealed record AssetReference(string ContentId, string Field, string Path, AssetRole Role);

internal enum AssetProblem
{
    None,
    Missing,
    InvalidPath,
    OutsideAllowedRoots,
    ReparsePoint,
    NotAFile,
}

internal sealed record AssetStatus(AssetReference Reference, AssetProblem Problem, long? Length, DateTime? LastWriteUtc, string? Detail);

internal sealed record FileProbeResult(bool Exists, bool IsDirectory, long Length, DateTime LastWriteUtc, bool HasReparsePoint);

/// <summary>Filesystem access for the asset checks; replaced by a fake in tests.</summary>
internal interface IFileProbe
{
    /// <param name="path">The file to inspect.</param>
    /// <param name="root">The allowed root; every directory between it and the file is checked for reparse points.</param>
    FileProbeResult Probe(WindowsPath path, WindowsPath root);
}

internal sealed record AssetReport(IReadOnlyList<AssetStatus> Items)
{
    public int Count(AssetProblem problem) => Items.Count(i => i.Problem == problem);

    public IEnumerable<AssetStatus> Blocking => Items.Where(i =>
        (i.Reference.Role == AssetRole.Required && i.Problem != AssetProblem.None)
        || i.Problem is AssetProblem.InvalidPath or AssetProblem.ReparsePoint);

    public long ExistingBytes => Items.Where(i => i.Problem == AssetProblem.None).Sum(i => i.Length ?? 0);
}

/// <summary>Every file a library snapshot points at, and whether it is safely present.</summary>
internal static class AssetInventory
{
    public static readonly IReadOnlyList<string> PathColumns = ["video_path", "image_path", "thumbnail_path", "film_reel_path"];

    /// <summary>Metadata keys that name local files Medal reuses.</summary>
    public static readonly IReadOnlySet<string> OptionalMetadataPaths = new HashSet<string>(StringComparer.Ordinal) { "$.keyboardOverlayFilePath" };

    /// <summary>
    /// <c>remoteContent</c> is Medal's cloud record, including where a file was when
    /// it was uploaded. It is history, not a local reference, so it is skipped.
    /// </summary>
    private const string RemoteRecordPrefix = "$.remoteContent";

    public static IReadOnlyList<AssetReference> Collect(LibrarySnapshot snapshot)
    {
        var contents = snapshot.Table("contents");
        int id = contents.ColumnIndex("local_content_id");
        int metadata = contents.ColumnIndex("metadata");
        var columns = PathColumns.Select(c => (Name: c, Index: contents.ColumnIndex(c))).ToArray();
        var references = new List<AssetReference>();
        foreach (var row in contents.Rows)
        {
            string contentId = TableSnapshot.Text(row[id])!;
            foreach (var (name, index) in columns)
            {
                if (TableSnapshot.Text(row[index]) is { Length: > 0 } value)
                {
                    references.Add(new AssetReference(contentId, name, value, AssetRole.Required));
                }
            }
            if (row[metadata] is JsonbCell json)
            {
                using var document = JsonDocument.Parse(json.Json, new JsonDocumentOptions { MaxDepth = 256 });
                CollectEmbedded(document.RootElement, "$", contentId, references);
            }
        }
        return references;
    }

    private static void CollectEmbedded(JsonElement element, string path, string contentId, List<AssetReference> references)
    {
        if (path.StartsWith(RemoteRecordPrefix, StringComparison.Ordinal)) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectEmbedded(property.Value, $"{path}.{property.Name}", contentId, references);
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray()) CollectEmbedded(item, $"{path}[{index++}]", contentId, references);
                break;
            case JsonValueKind.String when LooksAbsolute(element.GetString()!):
                var role = OptionalMetadataPaths.Contains(path) ? AssetRole.Optional : AssetRole.Unrecognized;
                references.Add(new AssetReference(contentId, "metadata:" + path, element.GetString()!, role));
                break;
        }
    }

    internal static bool LooksAbsolute(string value) =>
        (value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/')
        || value.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Checks each reference against the allowed roots and the filesystem.</summary>
    public static AssetReport Check(IEnumerable<AssetReference> references, IReadOnlyList<WindowsPath> allowedRoots, IFileProbe probe)
    {
        var items = new List<AssetStatus>();
        foreach (var reference in references)
        {
            if (!WindowsPath.TryParse(reference.Path, out WindowsPath? path, out string? problem))
            {
                items.Add(new AssetStatus(reference, AssetProblem.InvalidPath, null, null, problem));
                continue;
            }
            WindowsPath? root = allowedRoots.FirstOrDefault(path.IsWithin);
            if (root is null)
            {
                items.Add(new AssetStatus(reference, AssetProblem.OutsideAllowedRoots, null, null,
                    $"not under {string.Join(" or ", allowedRoots.Select(r => r.Display))}"));
                continue;
            }
            var found = probe.Probe(path, root);
            AssetProblem result = found switch
            {
                { HasReparsePoint: true } => AssetProblem.ReparsePoint,
                { Exists: false } => AssetProblem.Missing,
                { IsDirectory: true } => AssetProblem.NotAFile,
                _ => AssetProblem.None,
            };
            items.Add(new AssetStatus(reference, result,
                found.Exists ? found.Length : null, found.Exists ? found.LastWriteUtc : null, null));
        }
        return new AssetReport(items);
    }
}

/// <summary>The real filesystem. Never follows or creates anything.</summary>
internal sealed class FileSystemProbe : IFileProbe
{
    public FileProbeResult Probe(WindowsPath path, WindowsPath root)
    {
        bool reparse = false;
        var current = root;
        foreach (string segment in path.Segments.Skip(root.Segments.Count))
        {
            current = current.Append(segment);
            var info = new FileInfo(current.Display);
            if (!info.Exists && !Directory.Exists(current.Display)) break;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                reparse = true;
                break;
            }
        }
        var file = new FileInfo(path.Display);
        bool isDirectory = !file.Exists && Directory.Exists(path.Display);
        return new FileProbeResult(file.Exists || isDirectory, isDirectory, file.Exists ? file.Length : 0,
            file.Exists ? file.LastWriteTimeUtc : default, reparse);
    }
}

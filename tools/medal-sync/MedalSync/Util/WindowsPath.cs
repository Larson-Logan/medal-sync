namespace MedalSync.Util;

/// <summary>
/// A strictly parsed absolute Windows path. Parsing is pure string work, so
/// it behaves the same on every OS the tests run on. Anything that could alias
/// another location is refused instead of normalized: relative segments,
/// device namespaces, alternate data streams and trailing dots or spaces.
/// </summary>
internal sealed class WindowsPath : IEquatable<WindowsPath>
{
    private WindowsPath(string root, IReadOnlyList<string> segments)
    {
        Root = root;
        Segments = segments;
        Display = segments.Count == 0 ? root : root + string.Join('\\', segments);
        Key = Display.ToUpperInvariant();
    }

    /// <summary>"D:\" or "\\server\share\" with the original casing.</summary>
    public string Root { get; }

    public IReadOnlyList<string> Segments { get; }

    /// <summary>Backslash-separated path with the original casing.</summary>
    public string Display { get; }

    /// <summary>Case-folded comparison key (Windows paths are case-insensitive).</summary>
    public string Key { get; }

    public static WindowsPath Parse(string value) =>
        TryParse(value, out WindowsPath? path, out string? problem)
            ? path
            : throw new ArgumentException($"'{value}' is not an acceptable path: {problem}", nameof(value));

    public static bool TryParse(string? value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WindowsPath? path,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "empty";
            return false;
        }
        string text = value.Replace('/', '\\');
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            problem = "device namespace paths are not accepted";
            return false;
        }
        string root;
        string rest;
        if (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] == '\\')
        {
            root = char.ToUpperInvariant(text[0]) + @":\";
            rest = text[3..];
        }
        else if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] unc = text[2..].Split('\\');
            if (unc.Length < 2 || unc[0].Length == 0 || unc[1].Length == 0)
            {
                problem = "a UNC path needs a server and a share";
                return false;
            }
            if (SegmentProblem(unc[0]) is not null || SegmentProblem(unc[1]) is not null)
            {
                problem = "the UNC server or share is not an acceptable path segment";
                return false;
            }
            root = $@"\\{unc[0]}\{unc[1]}\";
            rest = string.Join('\\', unc.Skip(2));
        }
        else
        {
            problem = "not an absolute drive or UNC path";
            return false;
        }
        string trimmed = rest.TrimEnd('\\');
        var segments = new List<string>();
        if (trimmed.Length > 0)
        {
            foreach (string segment in trimmed.Split('\\'))
            {
                string? segmentProblem = SegmentProblem(segment);
                if (segmentProblem is not null)
                {
                    problem = segmentProblem;
                    return false;
                }
                segments.Add(segment);
            }
        }
        path = new WindowsPath(root, segments);
        problem = null;
        return true;
    }

    /// <summary>True when this path equals <paramref name="root"/> or lies beneath it.</summary>
    public bool IsWithin(WindowsPath root)
    {
        if (!string.Equals(Root, root.Root, StringComparison.OrdinalIgnoreCase)) return false;
        if (root.Segments.Count > Segments.Count) return false;
        for (int i = 0; i < root.Segments.Count; i++)
        {
            if (!string.Equals(Segments[i], root.Segments[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>The same relative location under a different root.</summary>
    public WindowsPath Rebase(WindowsPath from, WindowsPath to)
    {
        if (!IsWithin(from)) throw new ArgumentException($"'{Display}' is not under '{from.Display}'.", nameof(from));
        return new WindowsPath(to.Root, [.. to.Segments, .. Segments.Skip(from.Segments.Count)]);
    }

    public WindowsPath Append(params string[] segments)
    {
        foreach (string segment in segments)
        {
            string? problem = SegmentProblem(segment);
            if (problem is not null) throw new ArgumentException($"'{segment}': {problem}", nameof(segments));
        }
        return new WindowsPath(Root, [.. Segments, .. segments]);
    }

    public override string ToString() => Display;

    public bool Equals(WindowsPath? other) => other is not null && Key == other.Key;

    public override bool Equals(object? obj) => Equals(obj as WindowsPath);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    private static string? SegmentProblem(string segment)
    {
        if (segment.Length == 0) return "empty path segment";
        if (segment is "." or "..") return "relative segments are not accepted";
        if (segment.EndsWith('.') || segment.EndsWith(' ')) return "a segment ends with a dot or space";
        if (segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '/', '\\']) >= 0) return "a segment contains a reserved character";
        if (segment.Any(char.IsControl)) return "a segment contains a control character";
        // Win32 recognizes DOS device names even with an extension (NUL.mp4),
        // spaces before the extension, or the superscript port digits.
        string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3])))
            return "Windows device names are not accepted";
        return null;
    }
}

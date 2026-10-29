namespace MedalSync.Cli;

/// <summary>
/// A tiny, strict parser: unknown or repeated options are errors, never ignored,
/// and an option's value cannot itself start with "--".
/// </summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    private Arguments(IReadOnlyList<string> words) => Words = words;

    public IReadOnlyList<string> Words { get; }

    public static Arguments Parse(IReadOnlyList<string> args, IReadOnlySet<string> flags, IReadOnlySet<string> valued)
    {
        var words = new List<string>();
        var parsed = new Arguments(words);
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                words.Add(arg);
                continue;
            }
            if (!flags.Contains(arg) && !valued.Contains(arg)) throw new UsageException($"Unknown option {arg}.");
            if (parsed._options.ContainsKey(arg)) throw new UsageException($"{arg} was given more than once.");
            string? value = null;
            if (valued.Contains(arg))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"{arg} needs a value.");
                }
                value = args[++i];
            }
            parsed._options[arg] = value;
        }
        return parsed;
    }

    public bool Flag(string name)
    {
        _used.Add(name);
        return _options.ContainsKey(name);
    }

    public string? Value(string name)
    {
        _used.Add(name);
        return _options.GetValueOrDefault(name);
    }

    public string Required(string name) => Value(name) ?? throw new UsageException($"{name} is required.");

    /// <summary>Rejects options that were parsed but mean nothing to the chosen command.</summary>
    public void RequireAllUsed()
    {
        var unused = _options.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unused.Count > 0) throw new UsageException($"Not valid for this command: {string.Join(", ", unused)}.");
    }
}

internal sealed class UsageException(string message) : Exception(message);

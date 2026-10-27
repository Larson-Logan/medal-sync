namespace MedalSync.Cli;

/// <summary>A tiny, strict parser: unknown options are errors, never ignored.</summary>
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
            if (flags.Contains(arg))
            {
                parsed._options[arg] = null;
            }
            else if (valued.Contains(arg))
            {
                if (i + 1 >= args.Count) throw new UsageException($"{arg} needs a value.");
                parsed._options[arg] = args[++i];
            }
            else
            {
                throw new UsageException($"Unknown option {arg}.");
            }
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

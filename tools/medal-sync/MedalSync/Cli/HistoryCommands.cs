using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Util;

namespace MedalSync.Cli;

/// <summary><c>history list</c> and <c>history diff</c>: read-only, safe while another PC holds the library.</summary>
[SupportedOSPlatform("windows")]
internal static class HistoryCommands
{
    public static int List(Arguments args, TextWriter output)
    {
        bool json = args.Flag("--json");
        int limit = args.Value("--limit") is { } text ? (int)Number("--limit", text, 1, GenerationHistory.MaxLimit) : GenerationHistory.DefaultLimit;
        long? before = args.Value("--before") is { } b ? Number("--before", b, 1, long.MaxValue) : null;
        args.RequireAllUsed();
        if (Open(output) is not { } opened) return 3;
        var (store, head) = opened;
        var list = GenerationHistory.List(store.Generations, head, limit, before, DateTimeOffset.UtcNow);
        output.WriteLine(json ? JsonSerializer.Serialize(list, Json.Options) : GenerationHistory.Render(list));
        return 0;
    }

    public static int Diff(Arguments args, TextWriter output)
    {
        bool json = args.Flag("--json");
        long from = Number("--from", args.Required("--from"), 0, long.MaxValue);
        long to = Number("--to", args.Required("--to"), 0, long.MaxValue);
        string? details = args.Value("--out");
        args.RequireAllUsed();
        if (details is not null && File.Exists(details)) throw new UsageException($"{details} already exists; choose a new file for the details.");
        if (Open(output) is not { } opened) return 3;
        var (store, head) = opened;
        var (diff, items) = GenerationHistory.Diff(store.Generations, head, from, to, DateTimeOffset.UtcNow);
        if (details is not null) GenerationHistory.WriteDetails(details, diff, items);
        output.WriteLine(json ? JsonSerializer.Serialize(diff, Json.Options) : GenerationHistory.Render(diff));
        if (details is not null && !json) output.WriteLine($"Per-clip details were written to {details}. {GenerationHistory.PrivacyNotice}");
        return 0;
    }

    private static long Number(string name, string value, long min, long max) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) && number >= min && number <= max
            ? number : throw new UsageException($"{name} must be a whole number from {min} to {max}.");

    /// <summary>
    /// Reads the journal once, without the writer lock; the head it names is what the report describes.
    /// Null (exit 3, as for <c>status</c>) when the shared library cannot be reached.
    /// </summary>
    private static (CoordinationStore Store, long Head)? Open(TextWriter output)
    {
        var state = LocalState.ForCurrentUser();
        var config = MedalSyncConfig.Load(state.ConfigPath);
        try
        {
            if (!Directory.Exists(config.CoordinationRoot)) throw new StorageOfflineException("The coordination root is missing.");
            var store = CoordinationStore.Open(config.LibraryCoordinationDirectory, config.LibraryId, config.AccountId);
            return (store, store.ReadJournal().CommittedGeneration);
        }
        catch (IOException)
        {
            output.WriteLine("Waiting for storage: the shared library's server cannot be reached right now, and history is read from it. Try again later.");
            return null;
        }
    }
}

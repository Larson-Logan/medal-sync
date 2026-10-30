using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Session;

namespace MedalSync.Platform;

/// <summary>
/// Raw evidence gathered on this PC. It can hold private values; only <see cref="DiagnosticSummary"/>
/// decides what leaves, and it reads errors by type only, never by message.
/// </summary>
internal sealed record DiagnosticFacts
{
    public required DateTimeOffset Now { get; init; }
    public required string ToolVersion { get; init; }
    public string? WindowsVersion { get; init; }

    public bool? SetupInstall { get; init; }
    public Exception? InstallError { get; init; }

    public Exception? MedalError { get; init; }
    public bool? MedalRunning { get; init; }
    public MedalCompatibilityReport? Compatibility { get; init; }
    public string? SignedInAccount { get; init; }
    public double? BitrateMbps { get; init; }

    public bool? RclonePresent { get; init; }
    public string? RcloneSha256 { get; init; }
    public bool? WinFspInstalled { get; init; }
    public Exception? DependencyError { get; init; }

    public bool ConfigPresent { get; init; }
    public MedalSyncConfig? Config { get; init; }
    public Exception? ConfigError { get; init; }

    public long? CacheFreeBytes { get; init; }
    public long? CacheTotalBytes { get; init; }
    public bool? SessionFits { get; init; }
    public Exception? CapacityError { get; init; }

    public bool? CoordinationReachable { get; init; }
    public OwnershipRecord? Journal { get; init; }
    public Exception? JournalError { get; init; }
    public DeviceIdentity? Device { get; init; }
    public int? PendingSwitchRequests { get; init; }
    public SessionPhase? Phase { get; init; }
    public IReadOnlyList<string> PendingCloudCopies { get; init; } = [];
    public DateTimeOffset? LastVerifiedAt { get; init; }

    /// <summary>Other values that must never appear: computer and user names, state and program folders.</summary>
    public IReadOnlyList<string> Private { get; init; } = [];
}

/// <summary>
/// A small, versioned report for support, built only from allowlisted fields. Identifiers become per-export
/// tokens (<c>library-1</c>, <c>pc-2</c>, <c>target-1</c>); paths, remotes, file names, library rows, configuration
/// files, credentials and log or error text are never included. As a final guard every string value is scrubbed
/// of every private value the facts know about.
/// </summary>
internal static partial class DiagnosticSummary
{
    public const int CurrentFormat = 1;
    public const string Kind = "medal-sync-diagnostic-summary";
    private const string Redacted = "[redacted]";

    public static string Build(DiagnosticFacts facts)
    {
        var tokens = new Tokens();
        var refusals = new SortedSet<string>(StringComparer.Ordinal);
        var report = new JsonObject
        {
            ["format"] = CurrentFormat,
            ["kind"] = Kind,
            ["generatedAt"] = facts.Now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["note"] = "Tokens such as library-1 and pc-2 are new for each export. Paths, names, remotes, clips, credentials and logs are never included.",
            ["medalSync"] = new JsonObject
            {
                ["version"] = Version(facts.ToolVersion),
                ["installation"] = facts.InstallError is { } installError ? Unavailable(installError)
                    : facts.SetupInstall switch { true => "setup", false => "portable", null => "unknown" },
            },
            ["windows"] = new JsonObject { ["version"] = Version(facts.WindowsVersion) },
            ["medal"] = Medal(facts, refusals),
            ["dependencies"] = Dependencies(facts, refusals),
            ["configuration"] = Configuration(facts, tokens, refusals),
            ["capacity"] = Capacity(facts, refusals),
            ["replication"] = Replication(facts, tokens, refusals),
            ["ownership"] = Ownership(facts, tokens, refusals),
        };
        report["refusals"] = new JsonArray([.. refusals.Select(r => JsonValue.Create(r))]);
        Scrub(report, Secrets(facts));
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Medal(DiagnosticFacts facts, ISet<string> refusals)
    {
        if (facts.MedalError is { } error)
        {
            refusals.Add("medal-not-found");
            return new JsonObject { ["status"] = Unavailable(error) };
        }
        var compatibility = facts.Compatibility;
        var medal = new JsonObject
        {
            ["status"] = compatibility is null ? "unknown" : "ok",
            ["app"] = Version(compatibility?.Versions.App),
            ["recorder"] = Version(compatibility?.Versions.Recorder),
            ["running"] = facts.MedalRunning,
        };
        if (compatibility is null) return medal;
        medal["executablePresent"] = compatibility.ExecutablePresent;
        medal["buildReviewed"] = compatibility.BuildReviewed;
        medal["schemaVersion"] = compatibility.SchemaVersion;
        medal["definitionsVersion"] = compatibility.DefinitionsVersion;
        medal["schemaFingerprint"] = compatibility.SchemaFingerprint is { } fingerprint && HexPattern().IsMatch(fingerprint) ? fingerprint : null;
        medal["schemaSupported"] = compatibility.SchemaSupported;
        medal["compatible"] = compatibility.Compatible;
        if (!compatibility.ExecutablePresent) refusals.Add("medal-executable-missing");
        if (!NativeBackupBuilds.HasReadableVersions(compatibility.Versions)) refusals.Add("medal-version-unreadable");
        if (compatibility.SchemaVersion is null) refusals.Add("medal-schema-unreadable");
        else if (!compatibility.SchemaSupported) refusals.Add("medal-schema-unsupported");
        return medal;
    }

    private static JsonObject Dependencies(DiagnosticFacts facts, ISet<string> refusals)
    {
        if (facts.RclonePresent == false) refusals.Add("rclone-missing");
        if (facts.WinFspInstalled == false) refusals.Add("winfsp-missing");
        return new JsonObject
        {
            ["status"] = facts.DependencyError is { } error ? Unavailable(error) : "ok",
            ["rclone"] = facts.RclonePresent switch { true => "present", false => "missing", null => "unknown" },
            ["rcloneSha256"] = facts.RcloneSha256 is { } hash && HexPattern().IsMatch(hash) ? hash.ToLowerInvariant() : null,
            ["winFsp"] = facts.WinFspInstalled switch { true => "installed", false => "missing", null => "unknown" },
        };
    }

    private static JsonObject Configuration(DiagnosticFacts facts, Tokens tokens, ISet<string> refusals)
    {
        if (!facts.ConfigPresent)
        {
            refusals.Add("not-configured");
            return new JsonObject { ["status"] = "missing" };
        }
        if (facts.Config is not { } config)
        {
            refusals.Add("configuration-invalid");
            return new JsonObject { ["status"] = facts.ConfigError is { } error ? Unavailable(error) : "unknown" };
        }
        bool? accountMatches = facts.SignedInAccount is { } account ? account == config.AccountId : null;
        if (accountMatches == false) refusals.Add("medal-account-mismatch");
        var replication = config.Replication;
        return new JsonObject
        {
            ["status"] = "ok",
            ["format"] = config.Format,
            ["library"] = tokens.For("library", config.LibraryId),
            ["accountMatchesMedal"] = accountMatches,
            ["storageMode"] = Name(config.StorageMode),
            ["primary"] = config.StorageMode == StorageMode.Local ? "local-folder" : config.PrimaryIsRclone ? "rclone-remote" : "smb-share",
            ["localCopy"] = replication?.LocalCopyRoot is not null,
            ["cloudTargets"] = new JsonArray([.. (replication?.Targets ?? []).Select(t => (JsonNode)new JsonObject
            {
                ["target"] = tokens.For("target", t.Name),
                ["required"] = t.Required,
            })]),
            ["conflictPolicy"] = replication is null ? null : Name(replication.ConflictPolicy),
            ["replicationTimeoutMinutes"] = replication?.TimeoutMinutes,
            ["replicationBandwidthLimited"] = replication is null ? null : replication.BandwidthLimitMiB is not null,
            ["maxSessionHours"] = config.MaxSessionHours,
            ["cacheMaxGiB"] = config.Mount.CacheMaxGiB,
            ["minFreeGiB"] = config.Mount.MinFreeGiB,
            ["transfers"] = config.Mount.Transfers,
            ["uploadLimitedWhileRecording"] = config.Mount.BandwidthLimitMiB is not null,
            ["uploadLimitedWhileFinishing"] = config.Mount.FinishBandwidthLimitMiB is not null,
            ["syncthingExclusions"] = config.SyncExclusions?.Count ?? 0,
        };
    }

    private static JsonObject Capacity(DiagnosticFacts facts, ISet<string> refusals)
    {
        if (facts.SessionFits == false) refusals.Add("cache-space-insufficient");
        return new JsonObject
        {
            ["status"] = facts.CapacityError is { } error ? Unavailable(error) : facts.CacheFreeBytes is null ? "unknown" : "ok",
            ["cacheFree"] = Size(facts.CacheFreeBytes),
            ["cacheTotal"] = Size(facts.CacheTotalBytes),
            ["bitrateMbps"] = facts.BitrateMbps is { } bitrate && double.IsFinite(bitrate) ? Math.Round(bitrate) : null,
            ["sessionFits"] = facts.SessionFits,
        };
    }

    private static JsonObject Ownership(DiagnosticFacts facts, Tokens tokens, ISet<string> refusals)
    {
        if (facts.Config is null) return new JsonObject { ["status"] = "not-configured" };
        if (facts.CoordinationReachable == false)
        {
            refusals.Add("storage-offline");
            return new JsonObject { ["status"] = "unreachable" };
        }
        if (facts.Journal is not { } journal)
        {
            refusals.Add("journal-unreadable");
            return new JsonObject { ["status"] = facts.JournalError is { } error ? Unavailable(error) : "unknown" };
        }
        // This PC is pc-1 so reports read the same way; another owner gets the next token.
        string thisPc = tokens.For("pc", facts.Device?.DeviceId ?? "this PC");
        if (journal.State == OwnershipState.RecoveryRequired) refusals.Add("recovery-required");
        return new JsonObject
        {
            ["status"] = "ok",
            ["state"] = Name(journal.State),
            ["owner"] = journal.Owner is not { } owner ? "none"
                : facts.Device is { } device && owner.DeviceId == device.DeviceId ? thisPc : tokens.For("pc", owner.DeviceId),
            ["thisPc"] = thisPc,
            ["thisPcHasIdentity"] = facts.Device is not null,
            ["committedGeneration"] = journal.CommittedGeneration,
            ["updatedAgo"] = Age(facts.Now - journal.UpdatedAt),
            ["phase"] = facts.Phase is { } phase ? Name(phase) : null,
            ["pendingSwitchRequests"] = facts.PendingSwitchRequests,
        };
    }

    /// <summary>Read from this PC's own state, so it is reported even while the shared journal is out of reach.</summary>
    private static JsonObject Replication(DiagnosticFacts facts, Tokens tokens, ISet<string> refusals)
    {
        if (facts.Config is null) return new JsonObject { ["status"] = "not-configured" };
        if (facts.PendingCloudCopies.Count > 0) refusals.Add("cloud-copy-pending");
        return new JsonObject
        {
            ["status"] = "ok",
            ["pendingCloudCopies"] = new JsonArray([.. facts.PendingCloudCopies.Select(n => JsonValue.Create(tokens.For("target", n)))]),
            ["lastVerifiedAgo"] = facts.LastVerifiedAt is { } verified ? Age(facts.Now - verified) : "never",
        };
    }

    /// <summary>Prints the report; with a path, first saves exactly those bytes to a new file and never replaces one.</summary>
    public static int Write(string report, string? path, TextWriter output)
    {
        if (path is not null) Util.AtomicFile.CreateNew(Path.GetFullPath(path), new System.Text.UTF8Encoding(false).GetBytes(report + "\n"));
        output.WriteLine(report);
        output.WriteLine(path is null
            ? "Preview only; nothing was saved or sent. Review it, then run 'medal-sync diagnostics --out FILE' to save it to a new file."
            : "Saved exactly the summary above to the new file. Review it before sharing; nothing was sent anywhere.");
        return 0;
    }

    /// <summary>Every private value the facts know about, longest first so a value inside another is still removed.</summary>
    private static IReadOnlyList<string> Secrets(DiagnosticFacts facts)
    {
        var values = new List<string?>(facts.Private) { facts.SignedInAccount, facts.Device?.DeviceId, facts.Device?.DisplayName };
        if (facts.Config is { } config)
        {
            values.AddRange([config.LibraryId, config.AccountId, config.CoordinationRoot, config.Remote, config.CaptureFolder, config.CacheRoot,
                config.RclonePath, config.Replication?.LocalCopyRoot, config.Replication?.RcloneConfigPath]);
            foreach (var target in config.Replication?.Targets ?? []) values.AddRange([target?.Name, target?.Remote]);
            foreach (var exclusion in config.SyncExclusions ?? []) values.AddRange([exclusion?.FolderId, exclusion?.Pattern]);
        }
        if (facts.Journal is { } journal)
            values.AddRange([journal.LibraryId, journal.AccountId, journal.Owner?.DeviceId, journal.Owner?.DisplayName, journal.SessionId]);
        return values.Where(v => v is not null && v.Trim().Length >= 3).Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v.Length).ToArray();
    }

    private static void Scrub(JsonNode? node, IReadOnlyList<string> secrets)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToArray())
                {
                    if (child is JsonValue value && value.TryGetValue(out string? text)) obj[key] = Clean(text, secrets);
                    else Scrub(child, secrets);
                }
                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue(out string? text)) array[i] = Clean(text, secrets);
                    else Scrub(array[i], secrets);
                }
                break;
        }
    }

    private static string Clean(string text, IReadOnlyList<string> secrets)
    {
        foreach (string secret in secrets) text = text.Replace(secret, Redacted, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>An error by kind only. Messages can hold paths, names or provider output.</summary>
    private static string Unavailable(Exception error) => "unavailable: " + error.GetType().Name;

    private static string? Version(string? value) =>
        value is null ? null : VersionPattern().IsMatch(value) ? value : "unrecognized";

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseUpper.ConvertName(value.ToString());

    private static string? Size(long? bytes) => bytes switch
    {
        null => null,
        < 10_000_000_000 => "under 10 GB",
        < 50_000_000_000 => "10-50 GB",
        < 200_000_000_000 => "50-200 GB",
        < 1_000_000_000_000 => "200-1000 GB",
        _ => "over 1000 GB",
    };

    private static string Age(TimeSpan age) => age switch
    {
        { Ticks: < 0 } => "in the future",
        { TotalHours: < 1 } => "under 1 hour",
        { TotalDays: < 1 } => "under 1 day",
        { TotalDays: < 7 } => "under 7 days",
        { TotalDays: < 30 } => "under 30 days",
        _ => "30 days or more",
    };

    [GeneratedRegex(@"\A[0-9][0-9A-Za-z.+-]{0,39}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[0-9A-Fa-f]{16,128}\z")]
    private static partial Regex HexPattern();

    /// <summary>Per-export synthetic tokens: the first value of each kind is 1, the next 2, and so on.</summary>
    private sealed class Tokens
    {
        private readonly Dictionary<(string, string), string> _tokens = [];
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public string For(string kind, string value)
        {
            if (_tokens.TryGetValue((kind, value), out string? token)) return token;
            int next = _counts.GetValueOrDefault(kind) + 1;
            _counts[kind] = next;
            return _tokens[(kind, value)] = $"{kind}-{next}";
        }
    }
}

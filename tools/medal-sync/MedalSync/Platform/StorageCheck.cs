using MedalSync.Config;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>What one read-only rclone check found.</summary>
internal enum StorageCheckResult
{
    Ok,

    /// <summary>The provider could not be reached. Waiting may fix it.</summary>
    Offline,

    /// <summary>The provider refused rclone's sign-in; it expired or was revoked.</summary>
    SignInRequired,

    /// <summary>The credential file is missing, unreadable, encrypted, or does not define the remote.</summary>
    ConfigurationProblem,

    /// <summary>The configured folder does not exist.</summary>
    FolderMissing,

    /// <summary>The primary folder exists but has no Medal Sync marker.</summary>
    MarkerMissing,

    /// <summary>The primary folder's marker names another library.</summary>
    WrongLibrary,

    /// <summary>rclone failed for another reason.</summary>
    Failed,
}

/// <param name="Name">"primary" for an rclone primary, otherwise the target's name.</param>
/// <param name="Destination">The rclone path that was checked; never credential contents.</param>
internal sealed record StorageCheckItem(string Name, string Destination, bool Primary, bool Required, StorageCheckResult Result, string Detail)
{
    /// <summary>
    /// Something only the user can fix. An outage is not, and neither is a missing target folder: the first copy creates it.
    /// </summary>
    public bool NeedsAction => Result switch
    {
        StorageCheckResult.Ok or StorageCheckResult.Offline => false,
        StorageCheckResult.FolderMissing => Primary,
        _ => true,
    };

    /// <summary>Blocks handoff when it fails: an rclone primary or a required target.</summary>
    public bool Essential => Primary || Required;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => Primary ? $"Primary storage {Destination}" : $"Cloud copy '{Name}' ({Destination})";
}

/// <summary>The latest storage check, kept on this PC for the tray and <c>status</c>.</summary>
internal sealed record StorageCheckReport(int Format, string LibraryId, DateTimeOffset CheckedAt, IReadOnlyList<StorageCheckItem> Remotes)
{
    public const int CurrentFormat = 1;

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<StorageCheckItem> NeedingAction => Remotes.Where(r => r.NeedsAction);

    /// <summary>0 when everything answered, 1 when something needs the user, 3 when the only problem is an outage.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ExitCode => Remotes.Any(r => r.NeedsAction) ? 1 : Remotes.Any(r => r.Result == StorageCheckResult.Offline) ? 3 : 0;

    /// <summary>
    /// Problems still worth showing. A handoff verified after the check proves the primary and every target that
    /// is not pending again, so only the still-pending optional targets remain.
    /// </summary>
    public static IReadOnlyList<StorageCheckItem> Outstanding(LocalState state, string libraryId)
    {
        if (ReadLast(state, libraryId) is not { } report) return [];
        var problems = report.NeedingAction.ToList();
        if (StatusReport.ReadLastVerifiedAt(state.Root, libraryId) is { } verified && verified > report.CheckedAt)
        {
            var pending = StorageReplication.Pending(state, libraryId).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            problems.RemoveAll(p => p.Primary || !pending.Contains(p.Name));
        }
        return problems;
    }

    public string ToText()
    {
        if (Remotes.Count == 0) return "No rclone remotes are configured; there is nothing to check.";
        var lines = new List<string> { $"Checked {Remotes.Count} rclone remote(s) at {CheckedAt:yyyy-MM-dd HH:mm} UTC." };
        foreach (var item in Remotes)
        {
            string result = item.Result switch
            {
                StorageCheckResult.Ok => "OK",
                StorageCheckResult.Offline => "Offline",
                StorageCheckResult.SignInRequired => "Sign in again",
                StorageCheckResult.ConfigurationProblem => "Configuration",
                StorageCheckResult.FolderMissing => "Folder missing",
                StorageCheckResult.MarkerMissing => "Marker missing",
                StorageCheckResult.WrongLibrary => "Other library",
                _ => "Failed",
            };
            lines.Add($"  {result}: {item.Detail}{(item.Essential ? "" : " (optional copy)")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal static string PathFor(LocalState state) => Path.Combine(state.Root, "replication", "storage-check.json");

    /// <summary>The last saved report for this library, or null when there is none or it cannot be read.</summary>
    public static StorageCheckReport? ReadLast(LocalState state, string libraryId)
    {
        try
        {
            var report = Json.ReadFile<StorageCheckReport>(PathFor(state), "Storage check");
            return report.Format == CurrentFormat && report.LibraryId == libraryId && report.Remotes is not null
                && report.Remotes.All(r => r is not null && Enum.IsDefined(r.Result)) ? report : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>Advisory: a failure to save never changes the result.</summary>
    public void Save(LocalState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor(state))!);
            AtomicFile.Replace(PathFor(state), Json.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The tray simply keeps the previous result. */ }
    }
}

/// <summary>
/// Read-only check of every rclone remote in the configuration: the credential file exists and opens without a
/// prompt, and one cheap authenticated listing or marker read succeeds. Never writes to a destination and never
/// keeps rclone's error output, which can contain tokens. Remotes are checked in parallel with short timeouts.
/// </summary>
internal static class StorageCheck
{
    /// <summary>How long one rclone call may take before it counts as an outage.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    public static bool HasRemotes(MedalSyncConfig config) => config.PrimaryIsRclone || config.Replication?.Targets.Count > 0;

    /// <param name="run">Runs one rclone command and returns its output; throws <see cref="RcloneFailedException"/>. Replaced in tests.</param>
    /// <param name="fileExists">Replaced in tests.</param>
    public static StorageCheckReport Run(MedalSyncConfig config, DateTimeOffset now, Func<IReadOnlyList<string>, string>? run = null,
        Func<string, bool>? fileExists = null, Action? requireTime = null)
    {
        var remotes = new List<(string Name, string Destination, bool Primary, bool Required)>();
        if (config.PrimaryIsRclone) remotes.Add(("primary", config.PrimaryCapture, true, true));
        foreach (var target in config.Replication?.Targets ?? [])
            remotes.Add((target.Name, target.Remote.TrimEnd('/'), false, target.Required));
        if (remotes.Count == 0) return new(StorageCheckReport.CurrentFormat, config.LibraryId, now, []);

        string credentials = Environment.ExpandEnvironmentVariables(config.Replication!.RcloneConfigPath!);
        if (!(fileExists ?? File.Exists)(credentials))
            return new(StorageCheckReport.CurrentFormat, config.LibraryId, now, remotes.Select(r => new StorageCheckItem(r.Name, r.Destination, r.Primary, r.Required,
                StorageCheckResult.ConfigurationProblem,
                $"The rclone credential file is missing at {credentials}. Copy it to this PC from the PC where you ran 'rclone config', or create it there again.")).ToArray());

        run ??= arguments => new RcloneRunner(config.ExpandedRclonePath).Run(arguments, CallTimeout, requireTime, captureOutput: true);
        var items = new StorageCheckItem[remotes.Count];
        Parallel.For(0, remotes.Count, i =>
        {
            var (name, destination, primary, required) = remotes[i];
            StorageCheckResult result;
            string detail;
            try { (result, detail) = primary ? CheckPrimary(config, destination, credentials, run) : CheckTarget(config, destination, credentials, run); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                (result, detail) = (StorageCheckResult.Failed, $"{(primary ? "Primary storage" : "Cloud copy at")} {destination} could not be checked. Check rclonePath and retry.");
            }
            items[i] = new StorageCheckItem(name, destination, primary, required, result, detail);
        });
        return new(StorageCheckReport.CurrentFormat, config.LibraryId, now, items);
    }

    /// <summary>
    /// What a launch should do with a report. Optional targets only ever produce a notice; an rclone primary or a
    /// required target refuses before ownership when <see cref="CredentialCheckPolicy.Refuse"/> is set. Outages never refuse.
    /// </summary>
    public static Session.LaunchPreflight Verdict(StorageCheckReport report, CredentialCheckPolicy policy)
    {
        var problems = report.NeedingAction.ToList();
        if (problems.Count == 0) return Session.LaunchPreflight.Proceed;
        var blocking = policy == CredentialCheckPolicy.Refuse ? problems.Where(p => p.Essential).ToList() : [];
        if (blocking.Count > 0)
            return new(null, "Medal was not started because storage needs attention first (replication.credentialCheck is \"REFUSE\"). "
                + string.Join(" ", blocking.Select(p => p.Detail)) + " Nothing was changed; the library stays Ready.");
        return new("Storage check: " + string.Join(" ", problems.Select(p => p.Detail)), null);
    }

    private static IReadOnlyList<string> Common(string command, string credentials, params string[] rest) =>
        [command, .. rest, "--config", credentials, "--ask-password=false",
            "--retries", "1", "--low-level-retries", "1", "--contimeout", "10s", "--timeout", "20s"];

    private static (StorageCheckResult, string) CheckPrimary(MedalSyncConfig config, string destination, string credentials,
        Func<IReadOnlyList<string>, string> run)
    {
        string where = $"Primary storage {destination}";
        string marker;
        try
        {
            marker = run(Common("cat", credentials, destination + "/" + MedalSyncConfig.StorageMarker, "--count", "256"));
        }
        catch (RcloneFailedException ex) when (ex.ExitCode is 3 or 4 && (ex.Hints & (RcloneHints.SignIn | RcloneHints.Config)) == 0)
        {
            // The marker is missing: tell an absent folder from an unprepared one.
            try { run(Common("lsf", credentials, destination, "--max-depth", "1", "--dirs-only")); }
            catch (RcloneFailedException missing) when (missing.ExitCode == 3 && (missing.Hints & (RcloneHints.SignIn | RcloneHints.Config)) == 0)
            {
                return (StorageCheckResult.FolderMissing,
                    $"{where} does not exist. Check the remote folder name, or run 'medal-sync storage init-primary --apply' to prepare a new primary.");
            }
            catch (RcloneFailedException other) { return Classify(other, where, destination, credentials); }
            return (StorageCheckResult.MarkerMissing,
                $"{where} has no Medal Sync marker. Run 'medal-sync storage init-primary --apply' if this is the right folder.");
        }
        catch (RcloneFailedException ex) { return Classify(ex, where, destination, credentials); }
        catch (OperationCanceledException) { return Stopped(where); }
        return marker.Trim() == config.LibraryId
            ? (StorageCheckResult.Ok, $"{where} accepted rclone's sign-in and identifies this library.")
            : (StorageCheckResult.WrongLibrary, $"{where} belongs to another library. Check the remote folder in this PC's configuration.");
    }

    private static (StorageCheckResult, string) CheckTarget(MedalSyncConfig config, string destination, string credentials,
        Func<IReadOnlyList<string>, string> run)
    {
        string where = $"Cloud copy at {destination}";
        string listing;
        try { listing = run(Common("lsf", credentials, destination, "--max-depth", "1", "--dirs-only")); }
        catch (RcloneFailedException ex) when (ex.ExitCode == 3 && (ex.Hints & (RcloneHints.SignIn | RcloneHints.Config | RcloneHints.Network)) == 0)
        {
            return (StorageCheckResult.FolderMissing,
                $"{where}: the folder does not exist yet. The next finish creates it; check the folder name if you expected an existing copy.");
        }
        catch (RcloneFailedException ex) { return Classify(ex, where, destination, credentials); }
        catch (OperationCanceledException) { return Stopped(where); }
        bool hasCopy = listing.Split('\n').Any(line => line.Trim().TrimEnd('/') == config.LibraryId);
        return (StorageCheckResult.Ok, hasCopy
            ? $"{where} accepted rclone's sign-in."
            : $"{where} accepted rclone's sign-in; it holds no copy of this library yet. The next finish creates one.");
    }

    private static (StorageCheckResult, string) Classify(RcloneFailedException ex, string where, string destination, string credentials)
    {
        if (RcloneFailure.IsTransient(ex)) return (StorageCheckResult.Offline, $"{where} cannot be reached right now.");
        string detail = RcloneFailure.Describe(ex, where, destination, credentials);
        if (ex.Hints.HasFlag(RcloneHints.SignIn)) return (StorageCheckResult.SignInRequired, detail);
        if (ex.Hints.HasFlag(RcloneHints.Config)) return (StorageCheckResult.ConfigurationProblem, detail);
        return (StorageCheckResult.Failed, detail);
    }

    private static (StorageCheckResult, string) Stopped(string where) =>
        (StorageCheckResult.Offline, $"{where} was not checked: Windows is shutting down.");
}

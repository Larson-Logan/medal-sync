using System.Globalization;
using System.Text;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>How much history to keep. Defaults are deliberately generous.</summary>
/// <param name="Keep">Committed ancestors of the head, and newest local recovery folders of each kind, always kept.</param>
/// <param name="KeepDays">Anything newer than this many days is kept.</param>
/// <param name="GraceDays">Abandoned attempts and interrupted staging folders are kept at least this long.</param>
internal sealed record RetentionPolicy(int Keep = 20, int KeepDays = 90, int GraceDays = 30)
{
    public void Validate()
    {
        if (Keep is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(Keep));
        if (KeepDays is < 0 or > 3650) throw new ArgumentOutOfRangeException(nameof(KeepDays));
        if (GraceDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(GraceDays));
    }
}

/// <summary>One folder to remove. Server kinds name a folder in <c>generations/</c>; local kinds a path under the state root.</summary>
internal sealed record RetentionItem(string Kind, string Name, long? Generation, DateTimeOffset? CreatedAt, long Bytes)
{
    public bool OnServer => Kind is Retention.Generation or Retention.Abandoned or Retention.Staging or Retention.ServerTrash;
}

internal sealed record RetentionPlan(int Format, string Id, string LibraryId, string DeviceId, long Head, long Revision, DateTimeOffset CreatedAt,
    RetentionPolicy Policy, IReadOnlyList<long> Ancestry, IReadOnlyList<RetentionItem> Items, IReadOnlyList<string> Warnings, string Integrity = "")
{
    public const int CurrentFormat = 1;
}

/// <summary>
/// Reviewed pruning of old library generations on the shared server and of this PC's recovery folders. It runs only while
/// the library is Ready. The committed head, its recent ancestors, recent generations, each PC's latest committed
/// generation and the highest-numbered folder are always kept, and numbers stay increasing through a recorded
/// high-water mark. Folders are renamed into a trash folder first and deleted afterwards, so an interrupted apply leaves
/// whole folders or trash, never a half-deleted generation, and the same plan can be applied again.
/// </summary>
internal sealed class Retention(MedalSyncConfig config, LocalState state, DeviceIdentity device, CoordinationStore store, Func<DateTimeOffset>? clock = null)
{
    public const string Generation = "generation";
    public const string Abandoned = "abandoned-generation";
    public const string Staging = "staging";
    public const string ServerTrash = "server-trash";
    public const string AcceptedChanges = "accepted-changes";
    public const string MediaRefresh = "media-refresh";
    public const string AbortedStart = "aborted-start";
    public const string LocalTrash = "local-trash";
    private const string LocalTrashFolder = "retention-trash";
    private static readonly Version FirstAwareVersion = new(1, 3, 0);
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    private string GenerationsDirectory => store.Generations.Directory;

    public RetentionPlan Plan(RetentionPolicy policy, string? id = null)
    {
        policy.Validate();
        var journal = RequireReady();
        var now = Now;
        var keepAfter = now - TimeSpan.FromDays(policy.KeepDays);
        var graceAfter = now - TimeSpan.FromDays(policy.GraceDays);
        var items = new List<RetentionItem>();
        var warnings = new List<string>();

        List<(long Generation, GenerationManifest? Manifest)> chain;
        try { chain = store.Generations.Chain(journal.CommittedGeneration).ToList(); }
        catch (InvalidDataException ex)
        {
            throw new UnsafeOperationException($"The committed history cannot be followed ({ex.Message}); nothing was planned. Run 'medal-sync history list' to see where it breaks.");
        }
        var committed = chain.Select(c => c.Generation).ToHashSet();
        // The newest committed generation each PC published: its library may still match it, which lets that PC start without --replace-local-library.
        var latestPerDevice = chain.Where(c => c.Manifest is not null).GroupBy(c => c.Manifest!.Device.DeviceId)
            .Select(g => g.Max(c => c.Generation)).ToHashSet();
        foreach (var (number, manifest) in chain.Skip(1).Where(c => c.Manifest is not null).Skip(policy.Keep))
        {
            if (manifest!.CreatedAt > keepAfter || latestPerDevice.Contains(number)) continue;
            items.Add(new(Generation, GenerationStore.Name(number), number, manifest.CreatedAt, FolderBytes(Folder(number))));
        }
        foreach (var (number, manifest) in chain.Where(c => c.Manifest is not null && latestPerDevice.Contains(c.Generation)))
            if (!Version.TryParse(manifest!.ToolVersion.Split('-', '+')[0], out var version) || version < FirstAwareVersion)
                warnings.Add($"{manifest.Device.DisplayName} last published with medal-sync {manifest.ToolVersion}. Update every PC to {FirstAwareVersion} or later before applying, "
                    + "because older versions do not understand pruned history.");

        var numbered = store.Generations.Numbered();
        long highest = numbered.DefaultIfEmpty(-1).Max();
        foreach (long number in numbered.Where(n => !committed.Contains(n) && n != highest))
        {
            DateTimeOffset created;
            try { created = store.Generations.ReadManifest(number).CreatedAt; }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { created = Directory.GetLastWriteTimeUtc(Folder(number)); }
            if (created > graceAfter) continue;
            items.Add(new(Abandoned, GenerationStore.Name(number), number, created, FolderBytes(Folder(number))));
        }
        if (Directory.Exists(GenerationsDirectory))
        {
            foreach (string folder in Directory.EnumerateDirectories(GenerationsDirectory, ".staging-*"))
            {
                var created = new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder), TimeSpan.Zero);
                if (created <= graceAfter) items.Add(new(Staging, Path.GetFileName(folder), null, created, FolderBytes(folder)));
            }
            foreach (string folder in Directory.EnumerateDirectories(GenerationsDirectory, ".trash-*"))
                items.Add(new(ServerTrash, Path.GetFileName(folder), null, null, FolderBytes(folder)));
        }

        Local(items, journal, policy, keepAfter);
        var plan = new RetentionPlan(RetentionPlan.CurrentFormat, id ?? Guid.NewGuid().ToString("N"), config.LibraryId, device.DeviceId,
            journal.CommittedGeneration, journal.Revision, now, policy, chain.Select(c => c.Generation).ToArray(),
            items.OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal).ToArray(), warnings);
        if (store.ReadJournal() != journal) throw new UnsafeOperationException("The library changed during the review; preview again.");
        return plan with { Integrity = IntegrityOf(plan) };
    }

    private void Local(List<RetentionItem> items, OwnershipRecord journal, RetentionPolicy policy, DateTimeOffset keepAfter)
    {
        void Kind(string kind, string parent, string pattern, Func<string, bool> inUse)
        {
            if (!Directory.Exists(parent)) return;
            var folders = Directory.EnumerateDirectories(parent, pattern)
                .Where(f => !File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint))
                .Select(f => (Path: f, Created: new DateTimeOffset(Directory.GetCreationTimeUtc(f), TimeSpan.Zero)))
                .OrderByDescending(f => f.Created).ToArray();
            foreach (var folder in folders.Skip(policy.Keep))
            {
                if (folder.Created > keepAfter || inUse(folder.Path)) continue;
                items.Add(new(kind, Path.GetRelativePath(state.Root, folder.Path).Replace('\\', '/'), null, folder.Created, FolderBytes(folder.Path)));
            }
        }
        Kind(AcceptedChanges, state.BackupDirectory, "accepted-changes-*", _ => false);
        Kind(MediaRefresh, state.BackupDirectory, "media-refresh-*",
            f => journal.SessionId is { } session && Path.GetFileName(f) == "media-refresh-" + session);
        Kind(AbortedStart, Path.Combine(state.Root, "aborted-starts"), "*", f => Path.GetFileName(f) == journal.SessionId || CacheInUse(f));
        string trash = Path.Combine(state.Root, LocalTrashFolder);
        if (Directory.Exists(trash))
            foreach (string folder in Directory.EnumerateDirectories(trash))
                items.Add(new(LocalTrash, Path.GetRelativePath(state.Root, folder).Replace('\\', '/'), null, null, FolderBytes(folder)));
    }

    /// <summary>Aborted-start evidence names the cache it left behind; it is kept while that cache still holds files.</summary>
    private static bool CacheInUse(string folder)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "recovery.json")));
            string? cache = document.RootElement.TryGetProperty("cache", out var value) ? value.GetString() : null;
            return cache is null || (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return true; // Unreadable evidence is kept for a person to look at.
        }
    }

    /// <summary>
    /// Applies a reviewed plan under the journal writer lock, so no PC can start or finish meanwhile. Every item must still
    /// be eligible under a fresh review; items already moved by an interrupted apply of this plan are finished.
    /// </summary>
    public string Apply(RetentionPlan plan, Action? requireIdle = null)
    {
        Validate(plan);
        if (plan.LibraryId != config.LibraryId || plan.DeviceId != device.DeviceId)
            throw new UnsafeOperationException("The retention plan was made for another library or PC; preview again on this PC.");
        using (store.AcquireWriterLock(TimeSpan.FromSeconds(10)))
        {
            var journal = RequireReady();
            if (journal.CommittedGeneration != plan.Head || journal.Revision != plan.Revision)
                throw new UnsafeOperationException("The library changed since the review; preview retention again. Nothing was removed.");
            var fresh = Plan(plan.Policy, plan.Id);
            var eligible = fresh.Items.Select(i => (i.Kind, i.Name)).ToHashSet();
            if (!fresh.Ancestry.SequenceEqual(plan.Ancestry))
                throw new UnsafeOperationException("The committed history differs from the review; preview retention again. Nothing was removed.");
            foreach (var item in plan.Items)
                if (Exists(item) && !eligible.Contains((item.Kind, item.Name)))
                    throw new UnsafeOperationException($"{item.Name} is no longer eligible for removal; preview retention again. Nothing was removed.");

            // Record first: a folder retention has claimed is never mistaken for damage, and its number is never reused.
            var old = store.Generations.ReadRetention();
            var now = Now;
            var pruned = (old?.Pruned ?? []).ToDictionary(p => p.Generation);
            foreach (var item in plan.Items.Where(i => i.Generation is not null && i.Kind is Generation or Abandoned))
                pruned.TryAdd(item.Generation!.Value, new(item.Generation.Value, item.Kind == Generation, now));
            long high = new[] { old?.HighWater ?? -1, store.Generations.Numbered().DefaultIfEmpty(-1).Max() }
                .Concat(plan.Items.Select(i => i.Generation ?? -1)).Max();
            store.Generations.WriteRetention(new(GenerationRetention.CurrentFormat, high, plan.Ancestry, pruned.Values.OrderBy(p => p.Generation).ToArray()));

            string serverTrash = Path.Combine(GenerationsDirectory, ".trash-" + plan.Id);
            Remove(plan.Items.Where(i => i.OnServer), item => Path.Combine(GenerationsDirectory, item.Name), serverTrash, requireIdle);
        }
        string localTrash = Path.Combine(state.Root, LocalTrashFolder, plan.Id);
        Remove(plan.Items.Where(i => !i.OnServer), item => Path.Combine(state.Root, item.Name.Replace('/', '\\')), localTrash, requireIdle);
        string receipt = Path.Combine(state.Root, "retention", plan.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
        AtomicFile.Replace(receipt, Json.Serialize(new
        {
            format = 1, plan.Id, plan.Head, removed = plan.Items.Count, bytes = plan.Items.Sum(i => i.Bytes), completedAt = Now,
        }));
        return receipt;
    }

    /// <summary>Renames each folder into the trash (one step on the same volume), then deletes the trash.</summary>
    private static void Remove(IEnumerable<RetentionItem> items, Func<RetentionItem, string> path, string trash, Action? requireIdle)
    {
        var list = items.ToArray();
        if (list.Length == 0) return;
        foreach (var item in list.Where(i => i.Kind is not (ServerTrash or LocalTrash)))
        {
            requireIdle?.Invoke();
            string source = path(item);
            if (!Directory.Exists(source)) continue; // Already moved by an interrupted apply.
            if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{item.Name} is a link; retention was refused.");
            string target = Path.Combine(trash, item.Kind, Path.GetFileName(source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);
        }
        foreach (var leftover in list.Where(i => i.Kind is ServerTrash or LocalTrash).Select(path).Append(trash))
        {
            requireIdle?.Invoke();
            if (Directory.Exists(leftover)) Directory.Delete(leftover, recursive: true);
        }
    }

    private bool Exists(RetentionItem item) =>
        Directory.Exists(item.OnServer ? Path.Combine(GenerationsDirectory, item.Name) : Path.Combine(state.Root, item.Name.Replace('/', '\\')));

    private OwnershipRecord RequireReady()
    {
        var journal = store.ReadJournal();
        if (journal.State != OwnershipState.Clean)
            throw new UnsafeOperationException("Retention runs only while the library is Ready. Finish or recover the current session first.");
        return journal;
    }

    private string Folder(long number) => Path.Combine(GenerationsDirectory, GenerationStore.Name(number));

    private static long FolderBytes(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string IntegrityOf(RetentionPlan plan) => Hashing.Sha256Hex(Json.Serialize(plan with { Integrity = "" }));

    private static readonly HashSet<string> Kinds = [Generation, Abandoned, Staging, ServerTrash, AcceptedChanges, MediaRefresh, AbortedStart, LocalTrash];

    private static void Validate(RetentionPlan plan)
    {
        if (plan.Format != RetentionPlan.CurrentFormat || plan.Items is null || plan.Ancestry is null || plan.Policy is null || plan.Warnings is null
            || !CoordinationStore.IsValidId(plan.Id) || plan.Integrity != IntegrityOf(plan))
            throw new InvalidDataException("The retention plan is incomplete, edited or from another version; preview again.");
        plan.Policy.Validate();
        foreach (var item in plan.Items)
        {
            if (item is null || item.Name is null || !Kinds.Contains(item.Kind) || item.Name.Split('/').Any(s => s is "" or "." or "..")
                || item.Name.Contains('\\') || item.Name.Contains(':') || (item.OnServer && item.Name.Contains('/'))
                || (item.Kind is Generation or Abandoned && (item.Generation is not { } g || item.Name != GenerationStore.Name(g) || g == plan.Head))
                || (item.Kind == Generation && !plan.Ancestry.Contains(item.Generation!.Value)))
                throw new InvalidDataException("The retention plan contains an unsafe entry; preview again.");
        }
    }

    public static string Render(RetentionPlan plan)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Committed head: generation {plan.Head}. Keeping it, its {plan.Policy.Keep} newest ancestors, anything from the last {plan.Policy.KeepDays} day(s), "
            + $"each PC's latest committed generation and the highest-numbered folder. Abandoned or interrupted attempts are kept for {plan.Policy.GraceDays} day(s).");
        if (plan.Items.Count == 0) text.AppendLine("Nothing to remove.");
        foreach (var group in plan.Items.GroupBy(i => i.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var numbers = group.Where(i => i.Generation is not null).Select(i => i.Generation!.Value).Order().ToArray();
            string which = numbers.Length > 0 ? $" (generations {Ranges(numbers)})" : "";
            text.AppendLine(CultureInfo.InvariantCulture, $"  {Label(group.Key)}: {group.Count()} folder(s), {Storage.StorageVolume.Gb(group.Sum(i => i.Bytes))}{which}");
        }
        if (plan.Items.Any(i => i.Kind == Generation))
            text.AppendLine("A PC whose library matches only a removed generation will need 'medal-sync launch --replace-local-library' (a backup is kept). "
                + "Removed generations no longer appear in history list or history diff.");
        foreach (string warning in plan.Warnings) text.AppendLine("Warning: " + warning);
        return text.ToString().TrimEnd();
    }

    private static string Label(string kind) => kind switch
    {
        Generation => "old committed generations",
        Abandoned => "abandoned or unfinished attempts",
        Staging => "interrupted publish folders",
        ServerTrash or LocalTrash => "leftover trash from an interrupted retention",
        AcceptedChanges => "accepted-change reviews on this PC",
        MediaRefresh => "media refresh backups on this PC",
        AbortedStart => "aborted-start evidence on this PC",
        _ => kind,
    };

    internal static string Ranges(IReadOnlyList<long> sorted)
    {
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count; i++)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(i == j ? sorted[i].ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{sorted[i]}-{sorted[j]}"));
            i = j;
        }
        return string.Join(", ", parts);
    }
}

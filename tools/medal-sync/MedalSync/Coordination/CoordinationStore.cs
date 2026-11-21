using System.Text.RegularExpressions;
using MedalSync.Library;
using MedalSync.Config;
using MedalSync.Util;

namespace MedalSync.Coordination;

/// <summary>Identity marker written once when the shared library is created.</summary>
internal sealed record LibraryIdentity(int Format, string LibraryId, string AccountId, DateTimeOffset CreatedAt, DeviceIdentity CreatedBy)
{
    public const int CurrentFormat = 1;
}

internal sealed record HandoffRequest(int Format, DeviceIdentity Device, string RequestId, DateTimeOffset RequestedAt)
{
    public const int CurrentFormat = 1;

    /// <summary>After this, a request no longer prompts the owner: the PC that asked stopped waiting long ago, or no longer exists.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    private static readonly TimeSpan CloudClockSkew = TimeSpan.FromMinutes(5);

    public bool IsFreshInCloud(DateTimeOffset now) => now - RequestedAt < Lifetime && RequestedAt - now <= CloudClockSkew;
}

/// <summary>
/// The owner-only coordination directory on the server, for one library:
/// <c>library.json</c> (identity), <c>journal.json</c> (ownership),
/// <c>writer.lock</c>, <c>requests/</c> and <c>generations/</c>.
/// The lock only serializes journal updates for a few seconds at a time; the
/// journal itself is the ownership, so a dropped connection cannot release it.
/// A PC waiting for a handoff never holds the lock.
/// </summary>
internal sealed partial class CoordinationStore
{
    private CloudCoordination? cloud;

    public static CoordinationStore Open(MedalSyncConfig config, Action? requireTime = null)
    {
        if (config.StorageMode != StorageMode.Cloud) return Open(config.LibraryCoordinationDirectory, config.LibraryId, config.AccountId);
        return OpenCloud(config, CloudCoordination.For(config, requireTime));
    }

    internal static CoordinationStore OpenCloud(MedalSyncConfig config, CloudCoordination backend)
    {
        var value = backend.Read().Value;
        var store = new CoordinationStore(config.LibraryCoordinationDirectory, value.Identity, backend);
        _ = store.ReadJournal();
        return store;
    }

    public static void Create(MedalSyncConfig config, LibrarySnapshot snapshot, AssetManifest assets,
        DeviceIdentity device, string session, MedalVersions medal, DateTimeOffset now)
    {
        if (config.StorageMode == StorageMode.Cloud) CloudCoordination.For(config).Create(snapshot, assets, device, session, medal, now);
        else Create(config.LibraryCoordinationDirectory, config.LibraryId, snapshot, assets, device, session, medal, now);
    }
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(200);

    private CoordinationStore(string root, LibraryIdentity identity, CloudCoordination? backend = null)
    {
        cloud = backend;
        Root = root;
        Identity = identity;
        Generations = new GenerationStore(Path.Combine(root, "generations"), identity.LibraryId, identity.AccountId,
            backend is null ? null : () => backend.HighWater);
    }

    public string Root { get; }

    public LibraryIdentity Identity { get; }

    public GenerationStore Generations { get; }

    /// <summary>Discover cloud attempts before allocating an idle checkpoint, just as BeginPublish does at finish.</summary>
    internal void RefreshGenerationNumbers(Action? check = null) => cloud?.RefreshGenerationNumbers(check);

    private string JournalPath => Path.Combine(Root, "journal.json");

    /// <summary>Refuses a stale copy of the journal that Windows serves from a cached open; replaced in tests.</summary>
    internal FreshCoordinationRead JournalReader { get; set; } = FreshCoordinationRead.Default;

    /// <summary>Requests are replaced independently of the journal and use their own per-path read pauses.</summary>
    internal FreshCoordinationRead RequestReader { get; set; } = FreshCoordinationRead.Default;

    private string LockPath => Path.Combine(Root, "writer.lock");

    private string RequestsPath => Path.Combine(Root, "requests");

    public static bool IsValidId(string? value) => value is not null && IdPattern().IsMatch(value);

    /// <summary>False for an outage. A provider that refuses access still counts as reachable.</summary>
    public bool IsReachable()
    {
        if (cloud is not null)
        {
            try { _ = cloud.Read(); return true; }
            catch (StorageActionRequiredException) { return true; }
            catch (IOException) { return false; }
        }
        try { return File.Exists(JournalPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Opens an existing coordination directory and checks its identity.</summary>
    public static CoordinationStore Open(string root, string expectedLibraryId, string expectedAccountId)
    {
        string marker = Path.Combine(root, "library.json");
        if (!File.Exists(marker))
        {
            if (IsUnfinishedInit(root))
                throw new UnsafeOperationException(
                    $"'{root}' holds an unfinished library initialization and no library marker. Run 'medal-sync init --apply' again on the PC that started it to complete it; nothing was started.");
            throw new UnsafeOperationException(
                $"No Medal library marker at '{root}'. The server share may be unreachable; nothing was started.");
        }
        var identity = Json.ReadFile<LibraryIdentity>(marker, "Library marker");
        if (identity.LibraryId != expectedLibraryId || identity.AccountId != expectedAccountId)
        {
            throw new IncompatibleLibraryException(
                $"'{root}' holds library {identity.LibraryId} for account {identity.AccountId}, not {expectedLibraryId} for {expectedAccountId}.");
        }
        return new CoordinationStore(root, identity);
    }

    /// <summary>
    /// Creates a new shared library from a reviewed snapshot as generation 0.
    /// Refuses if anything already exists at <paramref name="root"/>, except this device's own unfinished
    /// initialization of the same reviewed snapshot, which it completes. A library marker is never replaced.
    /// </summary>
    public static CoordinationStore Create(string root, string libraryId, LibrarySnapshot initial, AssetManifest assets,
        DeviceIdentity device, string session, MedalVersions medal, DateTimeOffset now) =>
        Create(root, libraryId, initial, assets, device, session, medal, now, beforeLock: null);

    /// <param name="beforeLock">Test seam: runs after the first check, before the writer lock, as another PC's init could.</param>
    internal static CoordinationStore Create(string root, string libraryId, LibrarySnapshot initial, AssetManifest assets,
        DeviceIdentity device, string session, MedalVersions medal, DateTimeOffset now, Action? beforeLock)
    {
        if (!IsValidId(libraryId)) throw new ArgumentException("A library ID is 32 lowercase hex characters.", nameof(libraryId));
        string marker = Path.Combine(root, "library.json");
        bool resume = Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
        if (resume && (File.Exists(marker) || !IsUnfinishedInit(root)))
        {
            throw new UnsafeOperationException($"'{root}' is not empty. An existing library is never reinitialized.");
        }
        beforeLock?.Invoke();
        Directory.CreateDirectory(root);
        var bootstrap = new CoordinationStore(root, new LibraryIdentity(LibraryIdentity.CurrentFormat, libraryId, initial.AccountId, now, device));
        LibraryIdentity identity;
        using (bootstrap.AcquireWriterLock(TimeSpan.FromSeconds(5)))
        {
            // Always checked again under the lock, even when the folder looked empty above: another PC's init can fill
            // it in between. Only the lock itself or this device's exact bootstrap of the same reviewed library is accepted.
            DateTimeOffset createdAt = bootstrap.ResumableCreatedAt(initial, assets, device, now);
            if (!bootstrap.Generations.Exists(0)) bootstrap.Generations.Publish(0, null, session, device, initial, assets, medal, createdAt);
            byte[] journal = Json.Serialize(Ownership.Initial(libraryId, initial.AccountId, createdAt));
            // An earlier version wrote the journal in place, so an interruption can leave a truncated copy of these bytes.
            if (!File.Exists(bootstrap.JournalPath)) CreateAtomically(bootstrap.JournalPath, journal);
            else if (!File.ReadAllBytes(bootstrap.JournalPath).AsSpan().SequenceEqual(journal)) AtomicFile.Replace(bootstrap.JournalPath, journal);
            identity = new LibraryIdentity(LibraryIdentity.CurrentFormat, libraryId, initial.AccountId, createdAt, device);
            CreateAtomically(marker, Json.Serialize(identity));
        }
        return new CoordinationStore(root, identity);
    }

    /// <summary>
    /// True when <paramref name="root"/> holds only what an interrupted <see cref="Create(string, string, LibrarySnapshot, AssetManifest, DeviceIdentity, string, MedalVersions, DateTimeOffset)"/>
    /// can leave: the writer lock, generation 0 or its staging folders, the initial journal and temporary files, and no library marker.
    /// </summary>
    internal static bool IsUnfinishedInit(string root)
    {
        try
        {
            if (!Directory.Exists(root) || File.Exists(Path.Combine(root, "library.json"))) return false;
            bool any = false;
            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                any = true;
                string name = Path.GetFileName(entry);
                bool known = name switch
                {
                    "writer.lock" or "journal.json" => File.Exists(entry),
                    "generations" => IsInitGenerationDirectory(entry),
                    _ => File.Exists(entry) && TemporaryMarkerName().IsMatch(name),
                };
                if (!known) return false;
            }
            return any;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static bool IsInitGenerationDirectory(string path) =>
        IsOrdinaryDirectory(path) && Directory.EnumerateFileSystemEntries(path)
            .All(entry => IsOrdinaryDirectory(entry) && UnfinishedGenerationName().IsMatch(Path.GetFileName(entry)));

    private static bool IsOrdinaryDirectory(string path) => Directory.Exists(path)
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    /// <summary>
    /// The creation time of this device's unfinished bootstrap of exactly <paramref name="initial"/> and <paramref name="assets"/>,
    /// or <paramref name="now"/> when generation 0 was never published. Anything else refuses.
    /// </summary>
    private DateTimeOffset ResumableCreatedAt(LibrarySnapshot initial, AssetManifest assets, DeviceIdentity device, DateTimeOffset now)
    {
        const string differs = "The unfinished library initialization in '{0}' was not started by this PC from the same reviewed library. Preserve it and recover before retrying; it was not changed.";
        if (!IsUnfinishedInit(Root)) throw new UnsafeOperationException($"'{Root}' is not empty. An existing library is never reinitialized.");
        DateTimeOffset createdAt = now;
        if (Generations.Exists(0))
        {
            GenerationContent original;
            try { original = Generations.Read(0); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                throw new UnsafeOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, differs, Root) + " Generation 0 is incomplete or damaged: " + ex.Message);
            }
            if (original.Manifest.Device.DeviceId != device.DeviceId || original.Manifest.ParentGeneration is not null
                || original.Snapshot.Digest() != initial.Digest()
                || !Json.Serialize(original.Assets).AsSpan().SequenceEqual(Json.Serialize(assets)))
                throw new UnsafeOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, differs, Root));
            createdAt = original.Manifest.CreatedAt;
        }
        else if (File.Exists(JournalPath))
        {
            throw new UnsafeOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, differs, Root) + " It has a journal but no generation 0.");
        }
        if (File.Exists(JournalPath))
        {
            byte[] expected = Json.Serialize(Ownership.Initial(Identity.LibraryId, initial.AccountId, createdAt));
            byte[] existing = File.ReadAllBytes(JournalPath);
            if (existing.Length > expected.Length || !expected.AsSpan(0, existing.Length).SequenceEqual(existing))
                throw new UnsafeOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, differs, Root) + " Its journal is not the initial one.");
        }
        return createdAt;
    }

    /// <summary>Writes a complete file under a temporary name, then moves it into place without ever replacing one.</summary>
    private static void CreateAtomically(string path, byte[] bytes)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        AtomicFile.CreateNew(temp, bytes);
        try { File.Move(temp, path, overwrite: false); }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    [GeneratedRegex(@"\A(?:0000000000|\.staging-0000000000-[0-9a-f]{32})\z")]
    private static partial Regex UnfinishedGenerationName();

    [GeneratedRegex(@"\A\.(?:journal|library)\.json\.[0-9a-f]{32}\.tmp\z")]
    private static partial Regex TemporaryMarkerName();

    public OwnershipRecord ReadJournal()
    {
        var record = cloud is null
            ? Json.Deserialize<OwnershipRecord>(JournalReader.Read(JournalPath, "library's ownership journal"), $"'{JournalPath}': Ownership journal")
            : cloud.ReadJournal();
        if (record.Format != OwnershipRecord.CurrentFormat) throw new InvalidDataException($"Journal format {record.Format} is not supported.");
        if (record.LibraryId != Identity.LibraryId || record.AccountId != Identity.AccountId)
        {
            throw new InvalidDataException("The ownership journal belongs to a different library.");
        }
        if (!Generations.Exists(record.CommittedGeneration))
        {
            throw new InvalidDataException($"The journal's committed generation {record.CommittedGeneration} is missing.");
        }
        return record;
    }

    /// <summary>Applies one transition under the writer lock, checking the revision it was based on.</summary>
    public OwnershipRecord Update(Func<OwnershipRecord, OwnershipRecord> transition, TimeSpan lockTimeout, Action? check = null)
    {
        if (cloud is not null) return cloud.Update(transition, check);
        using var _ = AcquireWriterLock(lockTimeout);
        var current = ReadJournal();
        var next = transition(current);
        if (ReferenceEquals(next, current)) return current;
        if (next.Revision != current.Revision + 1 || next.LibraryId != current.LibraryId || next.AccountId != current.AccountId)
        {
            throw new InvalidOperationException("A journal transition must advance the revision by exactly one.");
        }
        if (next.CommittedGeneration != current.CommittedGeneration && !Generations.Exists(next.CommittedGeneration))
        {
            throw new InvalidDataException($"Generation {next.CommittedGeneration} must be published before it is committed.");
        }
        check?.Invoke();
        AtomicFile.Replace(JournalPath, Json.Serialize(next));
        var written = ReadJournal();
        if (written != next) throw new IOException("The ownership journal did not read back as written.");
        return written;
    }

    public IDisposable AcquireWriterLock(TimeSpan timeout)
    {
        if (cloud is not null) throw new UnsafeOperationException("This maintenance command requires an SMB writer lock and is unavailable in cloud mode. Cloud launch, finish, history and optional-copy retry use conditional journal updates.");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(LockRetry);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new StorageBusyException("Another PC is updating the Medal library right now. Try again in a moment.");
            }
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION; anything else is a real failure.</summary>
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    public void WriteRequest(HandoffRequest request)
    {
        if (cloud is not null) { cloud.Request(request); return; }
        if (!IsValidId(request.Device.DeviceId) || !IsValidId(request.RequestId)) throw new ArgumentException("Invalid request identity.", nameof(request));
        Directory.CreateDirectory(RequestsPath);
        AtomicFile.Replace(Path.Combine(RequestsPath, request.Device.DeviceId + ".json"), Json.Serialize(request));
    }

    /// <summary>Requests younger than <see cref="HandoffRequest.Lifetime"/>, oldest first.</summary>
    public IReadOnlyList<HandoffRequest> PendingRequests(DateTimeOffset now) => ReadRequests().Where(r => IsFresh(r, now)).Select(r => r.Request).ToList();

    /// <summary>Requests too old to prompt anyone; <see cref="RemoveStaleRequest"/> clears them.</summary>
    public IReadOnlyList<HandoffRequest> StaleRequests(DateTimeOffset now) => ReadRequests().Where(r => !IsFresh(r, now)).Select(r => r.Request).ToList();

    /// <summary>
    /// Share requests use the file time as well as the requesting PC's clock. Drive requests use the requesting
    /// PC's clock with a bounded allowance for skew, because a journal update can contain several requests.
    /// </summary>
    private bool IsFresh((HandoffRequest Request, DateTimeOffset WrittenAt) stored, DateTimeOffset now) => cloud is not null
        ? stored.Request.IsFreshInCloud(now)
        : now - stored.Request.RequestedAt < HandoffRequest.Lifetime && now - stored.WrittenAt < HandoffRequest.Lifetime;

    private List<(HandoffRequest Request, DateTimeOffset WrittenAt)> ReadRequests()
    {
        if (cloud is not null) return cloud.Read().Value.Requests.Select(r => (r, r.RequestedAt)).ToList();
        if (!Directory.Exists(RequestsPath)) return [];
        var requests = new List<(HandoffRequest, DateTimeOffset)>();
        foreach (string file in Directory.EnumerateFiles(RequestsPath, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(name)) continue;
            try
            {
                var request = Json.Deserialize<HandoffRequest>(RequestReader.Read(file, "switch request"), $"'{file}': Handoff request");
                if (request.Device?.DeviceId == name && request.Device.DisplayName is not null && IsValidId(request.RequestId))
                    requests.Add((request, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)));
            }
            catch (InvalidDataException)
            {
                // A half-written or foreign file is not a request.
            }
            catch (FileNotFoundException)
            {
                // Removed or replaced while being read.
            }
        }
        return requests.OrderBy(r => r.Item1.RequestedAt).ToList();
    }

    /// <summary>
    /// Removes a stale request without ever deleting a newer one from the same PC. The requesting PC replaces its file
    /// without the writer lock, so the file is first renamed aside and put back unless it is still exactly
    /// <paramref name="stale"/>. True when it was removed; a failure only leaves the request in place.
    /// </summary>
    public bool RemoveStaleRequest(HandoffRequest stale)
    {
        if (cloud is null) return RemoveExactRequest(stale);
        try { return cloud.Withdraw(stale); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MedalSyncException) { return false; }
    }

    /// <summary>
    /// Withdraws this PC's own <paramref name="request"/> when a wait for a handoff stops without starting Medal.
    /// Only that exact request is removed; a newer request from the same PC stays. True when it was removed.
    /// </summary>
    public bool WithdrawRequest(HandoffRequest request) => cloud is not null ? cloud.Withdraw(request) : RemoveExactRequest(request);

    private bool RemoveExactRequest(HandoffRequest stale)
    {
        if (!IsValidId(stale.Device.DeviceId)) return false;
        string path = Path.Combine(RequestsPath, stale.Device.DeviceId + ".json");
        string aside = Path.Combine(RequestsPath, $"{stale.Device.DeviceId}.{Guid.NewGuid():N}.removing");
        try
        {
            try { File.Move(path, aside); }
            catch (FileNotFoundException) { return false; }
            bool same;
            try { same = Json.ReadFile<HandoffRequest>(aside, "Handoff request") == stale; }
            catch (InvalidDataException) { same = false; }
            if (same)
            {
                File.Delete(aside);
                return true;
            }
            // A newer request arrived in between: put it back, unless an even newer one has already taken its place.
            try { File.Move(aside, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(aside); }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void RemoveRequest(DeviceIdentity device)
    {
        if (cloud is not null) { cloud.Request(new(1, device, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), remove: true); return; }
        if (!IsValidId(device.DeviceId)) throw new ArgumentException("Invalid device ID.", nameof(device));
        string path = Path.Combine(RequestsPath, device.DeviceId + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    [GeneratedRegex(@"\A[0-9a-f]{32}\z")]
    private static partial Regex IdPattern();
}

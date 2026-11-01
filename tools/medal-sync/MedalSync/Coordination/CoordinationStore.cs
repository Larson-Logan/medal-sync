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

    private string JournalPath => Path.Combine(Root, "journal.json");

    private string LockPath => Path.Combine(Root, "writer.lock");

    private string RequestsPath => Path.Combine(Root, "requests");

    public static bool IsValidId(string? value) => value is not null && IdPattern().IsMatch(value);

    /// <summary>False when the journal cannot be seen, which distinguishes an outage from a refusal.</summary>
    public bool IsReachable()
    {
        if (cloud is not null)
        {
            try { _ = cloud.Read(); return true; }
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
    /// Refuses if anything already exists at <paramref name="root"/>.
    /// </summary>
    public static CoordinationStore Create(string root, string libraryId, LibrarySnapshot initial, AssetManifest assets,
        DeviceIdentity device, string session, MedalVersions medal, DateTimeOffset now)
    {
        if (!IsValidId(libraryId)) throw new ArgumentException("A library ID is 32 lowercase hex characters.", nameof(libraryId));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new UnsafeOperationException($"'{root}' is not empty. An existing library is never reinitialized.");
        }
        Directory.CreateDirectory(root);
        var identity = new LibraryIdentity(LibraryIdentity.CurrentFormat, libraryId, initial.AccountId, now, device);
        var store = new CoordinationStore(root, identity);
        using (store.AcquireWriterLock(TimeSpan.FromSeconds(5)))
        {
            store.Generations.Publish(0, null, session, device, initial, assets, medal, now);
            AtomicFile.CreateNew(store.JournalPath, Json.Serialize(Ownership.Initial(libraryId, initial.AccountId, now)));
            AtomicFile.CreateNew(Path.Combine(root, "library.json"), Json.Serialize(identity));
        }
        return store;
    }

    public OwnershipRecord ReadJournal()
    {
        var record = cloud is null ? Json.ReadFile<OwnershipRecord>(JournalPath, "Ownership journal") : cloud.ReadJournal();
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
    public OwnershipRecord Update(Func<OwnershipRecord, OwnershipRecord> transition, TimeSpan lockTimeout)
    {
        if (cloud is not null) return cloud.Update(transition);
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
    /// Both the requesting PC's clock and the share's file time must say the request is recent, so a PC whose
    /// clock runs ahead cannot keep an old request alive. A time in the future counts as recent.
    /// </summary>
    private static bool IsFresh((HandoffRequest Request, DateTimeOffset WrittenAt) stored, DateTimeOffset now) =>
        now - stored.Request.RequestedAt < HandoffRequest.Lifetime && now - stored.WrittenAt < HandoffRequest.Lifetime;

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
                var request = Json.ReadFile<HandoffRequest>(file, "Handoff request");
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
        // Cloud requests are pruned under CAS whenever requests change; never remove a newer request by name.
        if (cloud is not null) return false;
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

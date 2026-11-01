using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Util;

namespace MedalSync.Platform;

internal sealed record CloudJournal(int Format, LibraryIdentity Identity, OwnershipRecord Journal, IReadOnlyList<HandoffRequest> Requests,
    string StorageFolderId, string CaptureFolder, string HeadManifestHash);
internal sealed record CloudJournalVersion(CloudJournal Value, string Etag);

/// <summary>
/// The journal is one Drive file's metadata, addressed by ID, never by name. Metadata and its v2 ETag are
/// read together; conditional PATCH serializes ownership. Media and immutable generations still use rclone.
/// No expiring lease or cached ownership can grant a session. Unsupported conditional writes fail closed.
/// </summary>
internal sealed class GoogleDriveJournal
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string fileId;
    private readonly Func<string> token;
    private readonly HttpClient client;
    private readonly Action? requireTime;
    private bool conditionalChecked;

    public GoogleDriveJournal(string fileId, Func<string> token, HttpClient? client = null, Action? requireTime = null)
    {
        if (!MedalSyncConfig.ValidCloudJournalId(fileId)) throw new InvalidDataException("Invalid Google Drive journal ID.");
        this.fileId = fileId; this.token = token; this.client = client ?? Client; this.requireTime = requireTime;
    }

    internal static GoogleDriveJournal For(MedalSyncConfig config, Action? requireTime = null) => new(config.CloudJournalId!,
        new DriveAccessToken(config.ExpandedRclonePath, config.Remote, config.Replication!.RcloneConfigPath!, requireTime).Get, requireTime: requireTime);

    private string FileUrl => "https://www.googleapis.com/drive/v2/files/" + fileId;

    public bool Exists()
    {
        using var response = Send(HttpMethod.Get, FileUrl + "?fields=id");
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        RequireSuccess(response);
        return true;
    }

    public static string GenerateId(string rclonePath, string remote, string credentials)
    {
        var api = new GoogleDriveJournal("unused-file-id", new DriveAccessToken(rclonePath, remote, credentials).Get);
        using var response = api.Send(HttpMethod.Get, "https://www.googleapis.com/drive/v2/files/generateIds?maxResults=1");
        RequireSuccess(response);
        using var json = ReadJson(response);
        string? id = json.RootElement.GetProperty("ids")[0].GetString();
        return MedalSyncConfig.ValidCloudJournalId(id) ? id! : throw new InvalidDataException("Google Drive returned an invalid journal ID.");
    }

    public CloudJournalVersion Read()
    {
        try { return ReadMetadata(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("Google Drive returned incomplete or unreadable journal metadata."); }
    }

    private CloudJournalVersion ReadMetadata()
    {
        using var response = Send(HttpMethod.Get, FileUrl + "?fields=id,mimeType,description,etag,labels(trashed)");
        RequireSuccess(response);
        using var json = ReadJson(response);
        var root = json.RootElement;
        if (root.GetProperty("id").GetString() != fileId || root.GetProperty("mimeType").GetString() != "application/json"
            || root.GetProperty("labels").GetProperty("trashed").GetBoolean())
            throw new InvalidDataException("The Google Drive journal is trashed or is not a Medal Sync journal file.");
        string? etag = root.GetProperty("etag").GetString();
        if (!EntityTagHeaderValue.TryParse(etag, out var parsed) || parsed.IsWeak || parsed.Tag == "*")
            throw new InvalidDataException("Google Drive did not return a strong journal ETag; ownership was refused.");
        string? description = root.GetProperty("description").GetString();
        var value = Json.Deserialize<CloudJournal>(Encoding.UTF8.GetBytes(description ?? ""), "Cloud journal");
        Validate(value);
        return new(value, etag!);
    }

    internal static void Validate(CloudJournal value)
    {
        if (value.Format != 1 || value.HeadManifestHash is not { Length: 64 } || !value.HeadManifestHash.All(char.IsAsciiHexDigit)
            || string.IsNullOrWhiteSpace(value.StorageFolderId) || !WindowsPath.TryParse(value.CaptureFolder, out _, out _)
            || value.Identity is not { Format: LibraryIdentity.CurrentFormat } identity
            || !CoordinationStore.IsValidId(identity.LibraryId) || string.IsNullOrEmpty(identity.AccountId)
            || value.Journal is not { Format: OwnershipRecord.CurrentFormat } journal || journal.LibraryId != identity.LibraryId
            || journal.AccountId != identity.AccountId || !Enum.IsDefined(journal.State) || journal.Revision < 1 || journal.CommittedGeneration < 0
            || (journal.State == OwnershipState.Clean ? journal.Owner is not null || journal.SessionId is not null
                : journal.Owner is null || !CoordinationStore.IsValidId(journal.Owner.DeviceId) || !CoordinationStore.IsValidId(journal.SessionId))
            || value.Requests is null || value.Requests.Count > 32 || value.Requests.Any(r => r is null || r.Format != 1
                || r.Device is null || !CoordinationStore.IsValidId(r.Device.DeviceId) || !CoordinationStore.IsValidId(r.RequestId))
            || value.Requests.Select(r => r.Device.DeviceId).Distinct(StringComparer.Ordinal).Count() != value.Requests.Count)
            throw new InvalidDataException("The cloud ownership journal is incomplete or belongs to another format.");
    }

    /// <summary>First prove that this endpoint refuses a mismatching ETag. The empty patch cannot replace journal data.</summary>
    private void RequireConditionalWrites()
    {
        if (conditionalChecked) return;
        using var response = Send(HttpMethod.Patch, FileUrl, "{}", "\"medal-sync-impossible-" + Guid.NewGuid().ToString("N") + "\"");
        if (response.StatusCode != HttpStatusCode.PreconditionFailed)
        {
            RequireSuccess(response);
            throw new UnsafeOperationException("Google Drive did not enforce conditional journal writes. Cloud handoff was refused; ownership was not changed.");
        }
        conditionalChecked = true;
    }

    public bool CompareExchange(CloudJournalVersion expected, CloudJournal next)
    {
        Validate(next);
        RequireConditionalWrites();
        string description = Encoding.UTF8.GetString(Json.Serialize(next));
        if (Encoding.UTF8.GetByteCount(description) > 30 * 1024) throw new InvalidDataException("Cloud journal exceeds its metadata size limit.");
        using var response = Send(HttpMethod.Patch, FileUrl, JsonSerializer.Serialize(new { description }), expected.Etag);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) return false;
        RequireSuccess(response);
        // A timeout or an unreadable response is never retried blindly. The next operation reads authoritative state.
        return true;
    }

    public void Create(CloudJournal initial, string parentId)
    {
        Validate(initial);
        using var response = Send(HttpMethod.Post, "https://www.googleapis.com/drive/v2/files", JsonSerializer.Serialize(new
        {
            id = fileId, title = "medal-sync-journal-" + initial.Identity.LibraryId + ".json", mimeType = "application/json",
            description = Encoding.UTF8.GetString(Json.Serialize(initial)), parents = new[] { new { id = parentId } },
        }));
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new UnsafeOperationException("This Google Drive journal ID already exists. Join the existing library; it was not reinitialized.");
        RequireSuccess(response);
        RequireConditionalWrites();
        if (!Json.Serialize(Read().Value).AsSpan().SequenceEqual(Json.Serialize(initial)))
            throw new IOException("The newly created Google Drive journal did not verify. Keep its ID and inspect it before retrying.");
    }

    private HttpResponseMessage Send(HttpMethod method, string url, string? body = null, string? etag = null)
    {
        requireTime?.Invoke();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (etag is not null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var pending = client.SendAsync(request, cancel.Token);
            while (!pending.IsCompleted) { requireTime?.Invoke(); Thread.Sleep(100); }
            var response = pending.GetAwaiter().GetResult();
            requireTime?.Invoke();
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new StorageOfflineException("Google Drive's ownership journal cannot be reached. Keep this PC's media and retry when connected.");
        }
        finally { cancel.Cancel(); }
    }

    private static JsonDocument ReadJson(HttpResponseMessage response)
    {
        byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Google Drive returned an oversized journal response.");
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new InvalidDataException("Google Drive returned an unreadable journal response."); }
    }

    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new StorageActionRequiredException("Google Drive refused access to the ownership journal. Check the Google account, Drive scope and rclone sign-in.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UnsafeOperationException("The Google Drive journal ID is missing or inaccessible. Check the shared cloudJournalId; initialize a new library only on its first PC.");
        throw new StorageOfflineException("Google Drive's ownership journal request failed. Ownership and media are retained; retry later.");
    }
}

/// <summary>Uses rclone's existing OAuth credentials and refresh logic. Config output remains in memory, never in logs.</summary>
internal sealed class DriveAccessToken(string rclone, string remote, string configPath, Action? requireTime = null,
    Func<IReadOnlyList<string>, string>? execute = null)
{
    private readonly string credentials = Environment.ExpandEnvironmentVariables(configPath);
    private string? accessToken;
    private DateTimeOffset expiry;
    private DateTime credentialWriteTime;

    private string Run(params string[] arguments) => (execute ?? (args => new RcloneRunner(rclone).Run(args,
        TimeSpan.FromSeconds(30), requireTime, captureOutput: true)))([.. arguments, "--config", credentials]);

    public string Get()
    {
        DateTime written = File.Exists(credentials) ? File.GetLastWriteTimeUtc(credentials) : DateTime.MinValue;
        if (accessToken is not null && credentialWriteTime == written && expiry > DateTimeOffset.UtcNow.AddMinutes(2)) return accessToken;
        if (!ReplicationConfig.ValidRemote(remote)) throw new InvalidDataException("Cloud coordination requires a named Google Drive remote and folder.");
        string name = remote[..remote.IndexOf(':')];
        string ReadToken()
        {
            using var dump = JsonDocument.Parse(Run("config", "dump"));
            if (!dump.RootElement.TryGetProperty(name, out var section) || !section.TryGetProperty("type", out var type) || type.GetString() != "drive"
                || !section.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
                throw new UnsafeOperationException("Cloud coordination requires a direct Google Drive rclone remote authorized with an OAuth token. Aliases, other providers and service accounts are not supported.");
            return token.GetString()!;
        }
        try
        {
            string text = ReadToken();
            using (var initial = JsonDocument.Parse(text))
                if (!initial.RootElement.TryGetProperty("expiry", out var expires) || !expires.TryGetDateTimeOffset(out expiry)
                    || expiry <= DateTimeOffset.UtcNow.AddMinutes(2))
                {
                    Run("about", remote[..(remote.IndexOf(':') + 1)], "--json", "--retries", "1", "--low-level-retries", "1");
                    text = ReadToken();
                }
            using var refreshed = JsonDocument.Parse(text);
            var root = refreshed.RootElement;
            if (!root.TryGetProperty("access_token", out var token) || string.IsNullOrWhiteSpace(token.GetString())
                || !root.TryGetProperty("expiry", out var end) || !end.TryGetDateTimeOffset(out expiry) || expiry <= DateTimeOffset.UtcNow)
                throw new InvalidDataException("Google Drive's OAuth token is missing or expired. Reconnect the rclone remote.");
            credentialWriteTime = File.Exists(credentials) ? File.GetLastWriteTimeUtc(credentials) : DateTime.MinValue;
            return accessToken = token.GetString()!;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("The Google Drive rclone credential configuration could not be read."); }
    }
}

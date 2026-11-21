using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MedalSync.Util;

namespace MedalSync.Platform;

internal sealed record CloudFileVersion(byte[] Bytes, string Version);

internal interface ICloudJournalFiles
{
    CloudFileVersion? Read(string id);
    bool Replace(string id, string version, byte[] bytes);
}

/// <summary>A provider's reserved file becomes the journal through the same conditional write used by handoff.</summary>
internal sealed class FileCloudJournal(string id, string storageId, ICloudJournalFiles files,
    Func<(string ItemId, string FolderId, Action Cleanup)> reserveProbe) : ICloudJournal
{
    internal static byte[] ReservedBytes => "Medal Sync reserved ownership journal.\n"u8.ToArray();
    private bool conditionalChecked;
    public bool HasMd5 => false;

    public bool Exists() => files.Read(id) is { } file && !file.Bytes.AsSpan().SequenceEqual(ReservedBytes);

    public CloudJournalVersion Read()
    {
        var file = RequireFile();
        var value = Json.Deserialize<CloudJournal>(file.Bytes, "Cloud ownership journal");
        CloudJournalProvider.Validate(value);
        return new(value, file.Version);
    }

    public bool CompareExchange(CloudJournalVersion expected, CloudJournal next)
    {
        RequireConditionalWrites();
        return files.Replace(id, expected.Version, Payload(next));
    }

    public void Create(CloudJournal initial, string parentId)
    {
        if (parentId != storageId) throw new UnsafeOperationException("The journal ID was reserved in a different storage folder.");
        var current = RequireFile();
        if (!current.Bytes.AsSpan().SequenceEqual(ReservedBytes))
            throw new UnsafeOperationException("This cloud journal already exists. Join the existing library; it was not reinitialized.");
        byte[] bytes = Payload(initial);
        RequireConditionalWrites();
        if (!files.Replace(id, current.Version, bytes)) throw new StorageBusyException("Another PC initialized the reserved cloud journal.");
        if (!RequireFile().Bytes.AsSpan().SequenceEqual(bytes))
            throw new IOException("The newly created cloud journal did not verify. Keep its ID and inspect it before retrying.");
    }

    private CloudFileVersion RequireFile() => files.Read(id)
        ?? throw new UnsafeOperationException("The cloud journal ID is missing or inaccessible. Check the shared cloudJournalId.");

    private static byte[] Payload(CloudJournal value)
    {
        CloudJournalProvider.Validate(value);
        byte[] bytes = Json.Serialize(value);
        if (bytes.Length > 30 * 1024) throw new InvalidDataException("Cloud journal exceeds its size limit.");
        return bytes;
    }

    private void RequireConditionalWrites()
    {
        if (conditionalChecked) return;
        var probe = reserveProbe();
        try
        {
            var original = files.Read(probe.ItemId);
            byte[] changed = "Medal Sync conditional write probe.\n"u8.ToArray();
            byte[] stale = "Medal Sync stale version probe.\n"u8.ToArray();
            if (original is null || !original.Bytes.AsSpan().SequenceEqual(ReservedBytes)
                || !files.Replace(probe.ItemId, original.Version, changed)
                || files.Replace(probe.ItemId, original.Version, stale)
                || files.Read(probe.ItemId) is not { } verified || verified.Version == original.Version
                || !verified.Bytes.AsSpan().SequenceEqual(changed))
                throw new UnsafeOperationException("The provider did not prove conditional journal writes. Cloud handoff was refused.");
            conditionalChecked = true;
        }
        finally
        {
            // A failed cleanup leaves only the uniquely named probe, never journal or media data.
            try { probe.Cleanup(); }
            catch (Exception ex) when (ex is IOException or MedalSyncException or UnauthorizedAccessException) { }
        }
    }
}

internal sealed class OneDriveJournalFiles(Func<string> token, Func<string> endpoint, Action? requireTime = null, HttpClient? client = null)
    : ICloudJournalFiles
{
    private readonly CloudJournalHttp http = new("OneDrive", token, requireTime, client);
    private string? graphEndpoint;

    private string Item(string key)
    {
        string[] ids = key.Split('#');
        if (ids.Length != 2 || ids.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid OneDrive journal item ID.");
        return (graphEndpoint ??= endpoint()) + "/v1.0/drives/" + Uri.EscapeDataString(ids[0]) + "/items/" + Uri.EscapeDataString(ids[1]);
    }

    public CloudFileVersion? Read(string id)
    {
        string item = Item(id);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string? tag = ReadTag(item);
            if (tag is null) return null;
            using var content = http.Send(HttpMethod.Get, item + "/content");
            if (content.StatusCode == HttpStatusCode.NotFound) return null;
            byte[] bytes;
            if (content.StatusCode == HttpStatusCode.Redirect)
            {
                var location = content.Headers.Location;
                if (location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps || location.UserInfo.Length != 0)
                    throw new InvalidDataException("OneDrive returned an invalid journal download URL.");
                using var download = http.Send(HttpMethod.Get, location.AbsoluteUri, authorize: false);
                http.RequireSuccess(download);
                bytes = CloudJournalHttp.Bytes(download);
            }
            else
            {
                http.RequireSuccess(content);
                bytes = CloudJournalHttp.Bytes(content);
            }
            // The version must describe the bytes, including when Graph redirects content to its CDN.
            if (ReadTag(item) == tag) return new(bytes, tag);
        }
        throw new StorageBusyException("OneDrive's journal changed while being read. Try again shortly.");
    }

    private string? ReadTag(string url)
    {
        using var response = http.Send(HttpMethod.Get, url + "?$select=eTag,file");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        http.RequireSuccess(response);
        using var document = CloudJournalHttp.Parse(CloudJournalHttp.Bytes(response));
        var item = document.RootElement;
        if (!item.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("eTag", out var tag) || tag.ValueKind != JsonValueKind.String
            || !EntityTagHeaderValue.TryParse(tag.GetString(), out var parsed) || parsed.IsWeak || parsed.Tag == "*")
            throw new InvalidDataException("OneDrive did not return a strong file ETag. Ownership was refused.");
        return parsed.ToString();
    }

    public bool Replace(string id, string version, byte[] bytes)
    {
        using var response = http.Send(HttpMethod.Put, Item(id) + "/content", bytes, "application/octet-stream",
            "If-Match", EntityTagHeaderValue.Parse(version).ToString());
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) return false;
        http.RequireSuccess(response);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new IOException("OneDrive did not acknowledge replacement of the existing journal item. Read its current version before retrying.");
        using var document = CloudJournalHttp.Parse(CloudJournalHttp.Bytes(response));
        if (!document.RootElement.TryGetProperty("id", out var observed) || observed.GetString() != id[(id.IndexOf('#') + 1)..])
            throw new InvalidDataException("OneDrive did not identify the updated journal item. Read its current version before retrying.");
        return true;
    }
}

internal sealed class DropboxJournalFiles(Func<string> token, Action? requireTime = null, HttpClient? client = null) : ICloudJournalFiles
{
    private const string Endpoint = "https://content.dropboxapi.com/2/files/";
    private readonly CloudJournalHttp http = new("Dropbox", token, requireTime, client);

    private static string RequireId(string id) => id.StartsWith("id:", StringComparison.Ordinal) && id.Length > 3
        ? id : throw new InvalidDataException("Invalid Dropbox journal item ID.");

    public CloudFileVersion? Read(string id)
    {
        using var response = http.Send(HttpMethod.Post, Endpoint + "download", header: "Dropbox-API-Arg",
            headerValue: JsonSerializer.Serialize(new { path = RequireId(id) }));
        if (response.StatusCode == HttpStatusCode.Conflict && ErrorIs(response, "path", "not_found")) return null;
        http.RequireSuccess(response);
        if (!response.Headers.TryGetValues("Dropbox-API-Result", out var values) || values.ToArray() is not [string metadata])
            throw new InvalidDataException("Dropbox did not return revision metadata with the journal bytes.");
        string version = ReadVersion(Encoding.UTF8.GetBytes(metadata), id);
        return new(CloudJournalHttp.Bytes(response), version);
    }

    public bool Replace(string id, string version, byte[] bytes)
    {
        string argument = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["path"] = RequireId(id),
            ["mode"] = new Dictionary<string, string> { [".tag"] = "update", ["update"] = version },
            ["autorename"] = false,
            ["strict_conflict"] = true,
            ["mute"] = true,
        });
        using var response = http.Send(HttpMethod.Post, Endpoint + "upload", bytes, "application/octet-stream", "Dropbox-API-Arg", argument);
        if (response.StatusCode == HttpStatusCode.Conflict && ErrorIs(response, "path", "conflict")) return false;
        http.RequireSuccess(response);
        _ = ReadVersion(CloudJournalHttp.Bytes(response), id);
        return true;
    }

    private static string ReadVersion(byte[] bytes, string id)
    {
        using var document = CloudJournalHttp.Parse(bytes);
        var metadata = document.RootElement;
        if (!metadata.TryGetProperty("id", out var fileId) || fileId.GetString() != id
            || !metadata.TryGetProperty("rev", out var rev) || rev.GetString() is not { Length: >= 9 and <= 128 } value
            || !value.All(char.IsAsciiLetterOrDigit))
            throw new InvalidDataException("Dropbox did not identify the journal file and its revision. Ownership was refused.");
        return value;
    }

    private static bool ErrorIs(HttpResponseMessage response, string outer, string inner)
    {
        using var document = CloudJournalHttp.Parse(CloudJournalHttp.Bytes(response));
        return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty(".tag", out var tag)
            && tag.GetString() == outer && error.TryGetProperty(outer, out var detail)
            && detail.TryGetProperty(".tag", out var reason) && reason.GetString() == inner;
    }
}

internal sealed class CloudJournalHttp(string provider, Func<string> token, Action? requireTime, HttpClient? client)
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxBytes };

    public HttpResponseMessage Send(HttpMethod method, string url, byte[]? bytes = null, string? contentType = null,
        string? header = null, string? headerValue = null, bool authorize = true)
    {
        requireTime?.Invoke();
        using var request = new HttpRequestMessage(method, url);
        if (authorize) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (header is not null) request.Headers.Add(header, headerValue);
        if (bytes is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType!);
        }
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var pending = (client ?? Client).SendAsync(request, cancel.Token);
            while (!pending.IsCompleted) { requireTime?.Invoke(); Thread.Sleep(100); }
            var response = pending.GetAwaiter().GetResult();
            try { requireTime?.Invoke(); }
            catch { response.Dispose(); throw; }
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new StorageOfflineException($"{provider}'s ownership journal could not be reached. Keep this PC's media and retry when connected.");
        }
        finally { cancel.Cancel(); }
    }

    internal static byte[] Bytes(HttpResponseMessage response)
    {
        byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The cloud provider returned an oversized journal response.");
        return bytes;
    }

    internal static JsonDocument Parse(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new InvalidDataException("The cloud provider returned unreadable journal metadata."); }
    }

    internal void RequireSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new StorageActionRequiredException($"{provider} refused journal access. Check this PC's account, permissions and rclone sign-in.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UnsafeOperationException($"The {provider} journal item is missing or inaccessible.");
        if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout))
            throw new StorageActionRequiredException($"{provider} refused the journal request (HTTP {(int)response.StatusCode}). Ownership was retained.");
        throw new StorageOfflineException($"{provider}'s ownership journal request failed. Ownership and media are retained; retry later.");
    }
}

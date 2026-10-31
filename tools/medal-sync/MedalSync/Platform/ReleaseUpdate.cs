using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MedalSync.Platform;

internal sealed record ReleaseAsset(string Version, string Name, string Url, string Sha256, long Size)
{
    public bool IsSetup => Name == $"medal-sync-{Version}-setup-win-x64.exe";
}

/// <summary>Best-effort public release discovery. Never installs or sends local state.</summary>
internal static partial class ReleaseUpdate
{
    internal const string LatestUrl = "https://api.github.com/repos/Larson-Logan/medal-sync/releases/latest";
    internal static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(2);

    // vMAJOR.MINOR.PATCH.NN, where NN is the two-digit hotfix counter. Releases
    // before 1.3.0.00 had no counter.
    [GeneratedRegex(@"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0[0-9]|[1-9][0-9]+))?\z")]
    private static partial Regex StableTag();

    internal static string? AvailableVersion(string json, string installed)
    {
        var asset = PublishedAsset(json);
        return asset is not null && Version.TryParse(installed, out var current)
            && Version.Parse(asset.Version) > current ? asset.Version : null;
    }

    internal static ReleaseAsset? PublishedAsset(string json, bool requireSetup = false)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string? tag = release.GetProperty("tag_name").GetString();
        if (tag is null || !StableTag().IsMatch(tag) || !Version.TryParse(tag[1..], out _)) return null;
        string setupName = $"medal-sync-{tag[1..]}-setup-win-x64.exe";
        bool hasSetup = release.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == setupName);
        if (requireSetup && !hasSetup) return null;
        string assetName = hasSetup ? setupName : $"medal-sync-{tag[1..]}-win-x64.zip";
        string assetUrl = $"https://github.com/Larson-Logan/medal-sync/releases/download/{tag}/{assetName}";
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == assetName).ToList();
        if (assets.Count != 1 || assets[0].GetProperty("browser_download_url").GetString() != assetUrl) return null;
        string? digest = assets[0].GetProperty("digest").GetString();
        if (digest is null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal)
            || digest[7..].Any(c => !char.IsAsciiHexDigit(c))) return null;
        long size = assets[0].GetProperty("size").GetInt64();
        if (size is < 1 or > 500 * 1024 * 1024) return null;
        return new ReleaseAsset(tag[1..], assetName, assetUrl, digest[7..], size);
    }

    internal static async Task<string?> CheckAsync(HttpClient client, string installed, CancellationToken cancellationToken)
    {
        var asset = await LatestAsync(client, cancellationToken).ConfigureAwait(false);
        return asset is not null && Version.TryParse(installed, out var current)
            && Version.Parse(asset.Version) > current ? asset.Version : null;
    }

    internal static async Task<ReleaseAsset?> LatestAsync(HttpClient client, CancellationToken cancellationToken, bool requireSetup = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.UserAgent.ParseAdd("MedalSync-Launcher");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            // No token: a private repository behaves like a missing release.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            // Bound both declared and actual response size before parsing.
            if (response.Content.Headers.ContentLength > 1024 * 1024) return null;
            await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken).ConfigureAwait(false);
            return PublishedAsset(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), requireSetup);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    internal static async Task<string> DownloadSetupAsync(HttpClient client, ReleaseAsset asset, string directory,
        Action<long, long>? progress, CancellationToken cancellationToken)
    {
        // Validate even when called independently of discovery. No caller-selected executable URL or filename.
        if (!StableTag().IsMatch("v" + asset.Version) || !Version.TryParse(asset.Version, out _)
            || !asset.IsSetup || asset.Url != $"https://github.com/Larson-Logan/medal-sync/releases/download/v{asset.Version}/{asset.Name}"
            || asset.Sha256.Length != 64 || asset.Sha256.Any(c => !char.IsAsciiHexDigit(c))
            || asset.Size is < 1 or > 500 * 1024 * 1024)
            throw new InvalidDataException("The release does not contain a verified Windows setup.");
        string destination = Path.Combine(directory, asset.Name);
        if (File.Exists(destination)) throw new IOException("A setup download already exists in this folder. Choose a new folder.");
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(0, asset.Size);
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new InvalidDataException("The setup download length does not match the release.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > asset.Size) throw new InvalidDataException("The setup download is larger than the release.");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    progress?.Invoke(received, asset.Size);
                }
                if (received != asset.Size) throw new InvalidDataException("The setup download is incomplete.");
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The setup download failed SHA-256 verification. Download it again.");
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, destination); // Never replace an earlier download.
            return destination;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public static void NotifyAtLaunch(Action<string> notify)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = LaunchTimeout };
        using var timeout = new CancellationTokenSource(LaunchTimeout);
        string? version = CheckAsync(client, ToolInfo.Version, timeout.Token).GetAwaiter().GetResult();
        if (version is not null)
            notify($"Medal Sync {version} is available. Use Check for Medal Sync updates in the Start menu to download the verified Windows setup. Quit Medal and wait for Ready before running setup.");
    }
}

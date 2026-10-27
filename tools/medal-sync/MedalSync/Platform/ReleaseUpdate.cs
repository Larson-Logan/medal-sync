using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LarsonServer.MedalSync.Platform;

/// <summary>Best-effort public release discovery. Never installs or sends local state.</summary>
internal static partial class ReleaseUpdate
{
    internal const string LatestUrl = "https://api.github.com/repos/Larson-Logan/medal-sync/releases/latest";
    internal static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex StableTag();

    internal static string? AvailableVersion(string json, string installed)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string? tag = release.GetProperty("tag_name").GetString();
        if (tag is null || !StableTag().IsMatch(tag) || !Version.TryParse(tag[1..], out var latest)
            || !Version.TryParse(installed, out var current) || latest <= current) return null;
        string setupName = $"medal-sync-{tag[1..]}-setup-win-x64.exe";
        bool hasSetup = release.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == setupName);
        string assetName = hasSetup ? setupName : $"medal-sync-{tag[1..]}-win-x64.zip";
        string assetUrl = $"https://github.com/Larson-Logan/medal-sync/releases/download/{tag}/{assetName}";
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == assetName).ToList();
        if (assets.Count != 1 || assets[0].GetProperty("browser_download_url").GetString() != assetUrl) return null;
        string? digest = assets[0].GetProperty("digest").GetString();
        if (digest is null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal)
            || digest[7..].Any(c => !char.IsAsciiHexDigit(c))) return null;
        return tag[1..];
    }

    internal static async Task<string?> CheckAsync(HttpClient client, string installed, CancellationToken cancellationToken)
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
            return AvailableVersion(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), installed);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public static void NotifyAtLaunch(Action<string> notify)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = LaunchTimeout };
        using var timeout = new CancellationTokenSource(LaunchTimeout);
        string? version = CheckAsync(client, ToolInfo.Version, timeout.Token).GetAwaiter().GetResult();
        if (version is not null)
            notify($"Medal Sync {version} is available. Quit Medal and wait for Ready, then download and run the Windows setup from GitHub Releases.");
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MedalSync.Platform;

internal sealed record ReleaseAsset(string Version, string Name, string Url, string Sha256, long Size)
{
    public bool IsSetup => Name == $"medal-sync-{Version}-setup-win-x64.exe";
}

/// <summary>Best-effort public release discovery and download housekeeping. Never installs or sends local state.</summary>
internal static partial class ReleaseUpdate
{
    internal const string LatestUrl = "https://api.github.com/repos/Larson-Logan/medal-sync/releases/latest";
    // Recent releases, newest first, for PCs that opted in to betas. The newest version is always among
    // them: a beta leads up to a version not yet released, and that release follows its betas.
    internal const string RecentUrl = "https://api.github.com/repos/Larson-Logan/medal-sync/releases?per_page=10";
    internal static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(2);

    // vMAJOR.MINOR.PATCH.NN, where NN is the two-digit hotfix counter. Releases
    // before 1.3.0.00 had no counter.
    [GeneratedRegex(@"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0[0-9]|[1-9][0-9]+))?\z")]
    private static partial Regex StableTag();

    // vMAJOR.MINOR.PATCH.NN-beta.N, a beta from dev of the release it leads up to.
    [GeneratedRegex(@"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0[0-9]|[1-9][0-9]+)-beta\.[1-9][0-9]*\z")]
    private static partial Regex BetaTag();

    // Any build version: a release, or a beta MAJOR.MINOR.PATCH.NN-beta.N.
    [GeneratedRegex(@"\A([0-9]+(\.[0-9]+){2,3})(-beta\.([1-9][0-9]*))?\z")]
    private static partial Regex BuildPattern();

    /// <summary>Orders builds by their numbers, then each beta before its release, then betas by number.</summary>
    private readonly record struct BuildVersion(Version Numbers, int Beta) : IComparable<BuildVersion>
    {
        public int CompareTo(BuildVersion other)
        {
            int order = Numbers.CompareTo(other.Numbers);
            return order != 0 ? order : Beta.CompareTo(other.Beta);
        }

        public static bool TryParse(string value, out BuildVersion version)
        {
            version = default;
            var match = BuildPattern().Match(value);
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var numbers)) return false;
            // A release sorts after every beta of it.
            int beta = int.MaxValue;
            if (match.Groups[4].Success && !int.TryParse(match.Groups[4].ValueSpan, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out beta)) return false;
            version = new(numbers, beta);
            return true;
        }
    }

    /// <summary>True when <paramref name="available"/> is newer than the installed build. A beta is older than its release.</summary>
    internal static bool IsNewer(string available, string installed) =>
        BuildVersion.TryParse(available, out var candidate) && BuildVersion.TryParse(installed, out var current)
        && candidate.CompareTo(current) > 0;

    internal static string? AvailableVersion(string json, string installed)
    {
        var asset = PublishedAsset(json);
        return asset is not null && IsNewer(asset.Version, installed) ? asset.Version : null;
    }

    /// <summary>One release from the latest-release endpoint. Only a published stable release qualifies.</summary>
    internal static ReleaseAsset? PublishedAsset(string json, bool requireSetup = false)
    {
        using var document = JsonDocument.Parse(json);
        return PublishedAsset(document.RootElement, requireSetup, allowBeta: false);
    }

    /// <summary>
    /// The newest qualifying release in a list of releases, for a PC that opted in to betas: a published stable
    /// release or a published beta prerelease, whichever version is newer.
    /// </summary>
    internal static ReleaseAsset? NewestAsset(string json, bool requireSetup = false)
    {
        using var document = JsonDocument.Parse(json);
        ReleaseAsset? newest = null;
        BuildVersion newestVersion = default;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var asset = PublishedAsset(release, requireSetup, allowBeta: true);
            if (asset is null || !BuildVersion.TryParse(asset.Version, out var version)) continue;
            if (newest is null || version.CompareTo(newestVersion) > 0) (newest, newestVersion) = (asset, version);
        }
        return newest;
    }

    private static ReleaseAsset? PublishedAsset(JsonElement release, bool requireSetup, bool allowBeta)
    {
        if (release.GetProperty("draft").GetBoolean()) return null;
        string? tag = release.GetProperty("tag_name").GetString();
        if (tag is null) return null;
        // A stable tag must be a full release, and a beta tag a prerelease that this PC opted in to.
        bool qualifies = release.GetProperty("prerelease").GetBoolean()
            ? allowBeta && BetaTag().IsMatch(tag) : StableTag().IsMatch(tag);
        if (!qualifies || !BuildVersion.TryParse(tag[1..], out _)) return null;
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

    /// <summary>
    /// Whether this PC opted in to betas with <c>betaUpdates</c> in config.json. Update checks also run before
    /// configuration, so a missing or unreadable configuration means stable releases only.
    /// </summary>
    internal static bool BetaUpdatesEnabled() => BetaUpdatesEnabled(Config.LocalState.ForCurrentUser().ConfigPath);

    internal static bool BetaUpdatesEnabled(string configPath)
    {
        try
        {
            return File.Exists(configPath) && Config.MedalSyncConfig.Load(configPath).BetaUpdates;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or MedalSyncException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static async Task<string?> CheckAsync(HttpClient client, string installed, CancellationToken cancellationToken, bool betas = false)
    {
        var asset = await LatestAsync(client, cancellationToken, betas: betas).ConfigureAwait(false);
        return asset is not null && IsNewer(asset.Version, installed) ? asset.Version : null;
    }

    /// <summary>The newest published release or, with <paramref name="betas"/>, the newest release or beta.</summary>
    internal static async Task<ReleaseAsset?> LatestAsync(HttpClient client, CancellationToken cancellationToken,
        bool requireSetup = false, bool betas = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, betas ? RecentUrl : LatestUrl);
            request.Headers.UserAgent.ParseAdd("MedalSync-Launcher");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            // No token: a private repository behaves like a missing release.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            // Bound both declared and actual response size before parsing.
            if (response.Content.Headers.ContentLength > 1024 * 1024) return null;
            await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return betas ? NewestAsset(json, requireSetup) : PublishedAsset(json, requireSetup);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>How long the setup download may deliver nothing before it is given up.</summary>
    internal static readonly TimeSpan DownloadStall = TimeSpan.FromSeconds(60);

    /// <param name="stall">
    /// Gives up, as a cancellation, when no response or no further bytes arrive for this long. The download as a
    /// whole has no time limit, so a slow connection that keeps delivering can finish. Null never gives up.
    /// </param>
    internal static async Task<string> DownloadSetupAsync(HttpClient client, ReleaseAsset asset, string directory,
        Action<long, long>? progress, CancellationToken cancellationToken, TimeSpan? stall = null)
    {
        // Validate even when called independently of discovery. No caller-selected executable URL or filename.
        if (!(StableTag().IsMatch("v" + asset.Version) || BetaTag().IsMatch("v" + asset.Version))
            || !BuildVersion.TryParse(asset.Version, out _)
            || !asset.IsSetup || asset.Url != $"https://github.com/Larson-Logan/medal-sync/releases/download/v{asset.Version}/{asset.Name}"
            || asset.Sha256.Length != 64 || asset.Sha256.Any(c => !char.IsAsciiHexDigit(c))
            || asset.Size is < 1 or > 500 * 1024 * 1024)
            throw new InvalidDataException("The release does not contain a verified Windows setup.");
        string destination = Path.Combine(directory, asset.Name);
        if (File.Exists(destination)) throw new IOException("A setup download already exists in this folder. Choose a new folder.");
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        // Cancelled by the caller, or by the stall limit, which starts again whenever bytes arrive.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = watchdog.Token;
        void Alive() { if (stall is { } limit) watchdog.CancelAfter(limit); }
        try
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(0, asset.Size);
            Alive();
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new InvalidDataException("The setup download length does not match the release.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    Alive();
                    received += read;
                    if (received > asset.Size) throw new InvalidDataException("The setup download is larger than the release.");
                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    progress?.Invoke(received, asset.Size);
                }
                if (received != asset.Size) throw new InvalidDataException("The setup download is incomplete.");
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The setup download failed SHA-256 verification. Download it again.");
                await file.FlushAsync(token).ConfigureAwait(false);
            }
            // Everything arrived and verified; from here only the caller can still cancel.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, destination); // Never replace an earlier download.
            return destination;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    /// <summary>Per-user folder holding one subfolder per verified setup download.</summary>
    internal static string DownloadsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MedalSync", "Downloads");

    /// <summary>
    /// Finds an earlier download of exactly this setup that still matches the release size and SHA-256,
    /// so a postponed update does not download again. Anything else is ignored, never trusted.
    /// </summary>
    internal static string? FindVerifiedDownload(string root, ReleaseAsset asset)
    {
        if (!asset.IsSetup || !Directory.Exists(root)) return null;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var file = new FileInfo(Path.Combine(directory, asset.Name));
                if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != asset.Size) continue;
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    return file.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Download again instead. */ }
        return null;
    }

    [GeneratedRegex(@"\Amedal-sync-([0-9]+(\.[0-9]+){2,3}(-beta\.[1-9][0-9]*)?)-setup-win-x64\.exe\z")]
    private static partial Regex SetupFile();

    /// <summary>
    /// Best effort: removes download folders that hold only setups for this version or older, which are
    /// already installed. Folders with any other content, newer setups or links are left alone.
    /// </summary>
    internal static int CleanupDownloads(string root, string installed)
    {
        if (!BuildVersion.TryParse(installed, out var current) || !Directory.Exists(root)) return 0;
        int removed = 0;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var folder = new DirectoryInfo(directory);
                    if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint) || folder.EnumerateDirectories().Any()) continue;
                    var files = folder.GetFiles();
                    bool installedOnly = files.All(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && SetupFile().Match(file.Name) is { Success: true } match
                        && BuildVersion.TryParse(match.Groups[1].Value, out var version) && version.CompareTo(current) <= 0);
                    if (!installedOnly) continue;
                    foreach (var file in files) file.Delete();
                    folder.Delete();
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* In use; retry next time. */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Nothing to clean. */ }
        return removed;
    }

    public static void NotifyAtLaunch(Action<string> notify)
    {
        if (OperatingSystem.IsWindows()) CleanupDownloads(DownloadsRoot(), ToolInfo.Version);
        bool betas = BetaUpdatesEnabled();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = LaunchTimeout };
        using var timeout = new CancellationTokenSource(LaunchTimeout);
        string? version = CheckAsync(client, ToolInfo.Version, timeout.Token, betas).GetAwaiter().GetResult();
        if (version is not null)
            notify($"Medal Sync {version} is available. Choose Check for updates in the Medal Sync tray menu. It downloads the verified update and installs it for you after you quit Medal.");
    }
}

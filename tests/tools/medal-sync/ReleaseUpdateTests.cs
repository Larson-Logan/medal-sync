using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MedalSync.Platform;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

public sealed class ReleaseUpdateTests
{
    private static string Release(string version = "0.3.0", bool draft = false, bool prerelease = false, string? digest = null) =>
        JsonSerializer.Serialize(new
        {
            draft, prerelease, tag_name = $"v{version}",
            assets = new[] { new { name = $"medal-sync-{version}-win-x64.zip",
                browser_download_url = $"https://github.com/Larson-Logan/medal-sync/releases/download/v{version}/medal-sync-{version}-win-x64.zip",
                digest = digest ?? "sha256:" + new string('a', 64), size = 1024 } },
        });

    [Fact]
    public void FindsOnlyNewerStableVersionWithVerifiedPackage()
    {
        Assert.Equal("0.3.0", ReleaseUpdate.AvailableVersion(Release(), "0.2.0"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release(), "0.3.0"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release(), "0.4.0"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release(draft: true), "0.2.0"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release(prerelease: true), "0.2.0"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release(version: "0.3.0-beta"), "0.2.0"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release(digest: "sha256:bad"), "0.2.0"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release().Replace("https://github.com/", "https://example.com/"), "0.2.0"));
    }

    [Fact]
    public void ComparesHotfixCounterNumerically()
    {
        Assert.Equal("1.3.0.00", ReleaseUpdate.AvailableVersion(Release("1.3.0.00"), "1.2.0"));
        Assert.Equal("1.3.0.00", ReleaseUpdate.AvailableVersion(Release("1.3.0.00"), "1.3.0"));
        Assert.Equal("1.3.0.01", ReleaseUpdate.AvailableVersion(Release("1.3.0.01"), "1.3.0.00"));
        Assert.Equal("1.3.0.10", ReleaseUpdate.AvailableVersion(Release("1.3.0.10"), "1.3.0.09"));
        Assert.Equal("1.3.0.100", ReleaseUpdate.AvailableVersion(Release("1.3.0.100"), "1.3.0.99"));
        Assert.Equal("1.3.1.00", ReleaseUpdate.AvailableVersion(Release("1.3.1.00"), "1.3.0.12"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.0.01"), "1.3.0.01"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.0.01"), "1.3.0.02"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.0.05"), "1.3.1.00"));
    }

    [Fact]
    public void OffersTheReleaseABetaLeadsUpTo()
    {
        // Betas from dev are MAJOR.MINOR.PATCH.NN-beta.N of the next release.
        Assert.Equal("1.3.1.02", ReleaseUpdate.AvailableVersion(Release("1.3.1.02"), "1.3.1.02-beta.3"));
        Assert.Equal("1.3.2.00", ReleaseUpdate.AvailableVersion(Release("1.3.2.00"), "1.3.1.02-beta.1"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.1.01"), "1.3.1.02-beta.1"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.1.02"), "1.3.1.03-beta.1"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.1.02"), "1.3.1.02-rc.1"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.1.02"), "1.3.1.02-beta.0"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.1.02"), "unknown"));
    }

    [Fact]
    public void NeverOffersABetaWithoutOptingIn()
    {
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.1.02-beta.1", prerelease: true), "1.3.1.01"));
        Assert.Null(ReleaseUpdate.AvailableVersion(Release("1.3.1.02-beta.1"), "1.3.1.01"));
    }

    private static string Releases(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private static string? Newest(params string[] releases) => ReleaseUpdate.NewestAsset(Releases(releases))?.Version;

    [Fact]
    public void OptedInPcsGetTheNewestOfReleasesAndBetas()
    {
        string stable = Release("1.3.1.01");
        string beta1 = Release("1.3.1.02-beta.1", prerelease: true);
        string beta2 = Release("1.3.1.02-beta.2", prerelease: true);
        Assert.Equal("1.3.1.02-beta.2", Newest(beta2, beta1, stable));
        Assert.Equal("1.3.1.02-beta.2", Newest(stable, beta1, beta2));
        // The release a beta leads up to replaces it.
        Assert.Equal("1.3.1.02", Newest(Release("1.3.1.02"), beta2, beta1, stable));
        Assert.Equal("1.3.1.01", Newest(stable, Release("1.3.1.02-beta.3", draft: true, prerelease: true)));
        Assert.Null(Newest());
    }

    [Fact]
    public void OptedInPcsStillRefuseMislabeledOrMalformedBetas()
    {
        string stable = Release("1.3.1.01");
        // A beta tag must be a prerelease, and a prerelease must carry a beta tag.
        Assert.Equal("1.3.1.01", Newest(stable, Release("1.3.1.02-beta.1")));
        Assert.Equal("1.3.1.01", Newest(stable, Release("1.3.1.02", prerelease: true)));
        foreach (string version in new[] { "1.3.1-beta.1", "1.3.1.02-beta.0", "1.3.1.02-rc.1", "1.3.1.2-beta.1", "1.3.1.02-beta.01" })
            Assert.Equal("1.3.1.01", Newest(stable, Release(version, prerelease: true)));
        Assert.Equal("1.3.1.01", Newest(stable, Release("1.3.1.02-beta.1", prerelease: true, digest: "sha256:bad")));
    }

    [Fact]
    public void OrdersBetasBeforeTheirReleaseAndByNumber()
    {
        Assert.True(ReleaseUpdate.IsNewer("1.3.1.02-beta.2", "1.3.1.02-beta.1"));
        Assert.True(ReleaseUpdate.IsNewer("1.3.1.02-beta.10", "1.3.1.02-beta.9"));
        Assert.True(ReleaseUpdate.IsNewer("1.3.1.02-beta.1", "1.3.1.01"));
        Assert.True(ReleaseUpdate.IsNewer("1.3.1.02", "1.3.1.02-beta.5"));
        Assert.False(ReleaseUpdate.IsNewer("1.3.1.02-beta.5", "1.3.1.02"));
        Assert.False(ReleaseUpdate.IsNewer("1.3.1.02-beta.1", "1.3.1.02-beta.1"));
        Assert.False(ReleaseUpdate.IsNewer("1.3.1.02-beta.1", "1.3.1.03-beta.1"));
    }

    [Fact]
    public async Task OptedInChecksReadRecentReleasesAndDownloadBetaSetups()
    {
        using var fixture = new MedalFixture();
        byte[] bytes = [5, 6, 7];
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        const string Tag = "v1.3.1.02-beta.1";
        const string Name = "medal-sync-1.3.1.02-beta.1-setup-win-x64.exe";
        string url = $"https://github.com/Larson-Logan/medal-sync/releases/download/{Tag}/{Name}";
        string beta = JsonSerializer.Serialize(new
        {
            draft = false, prerelease = true, tag_name = Tag,
            assets = new[] { new { name = Name, browser_download_url = url, digest = "sha256:" + sha, size = bytes.LongLength } },
        });
        var requested = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsoluteUri == ReleaseUpdate.RecentUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Releases(beta, Release("1.3.1.01"))) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        Assert.Equal("1.3.1.02-beta.1", await ReleaseUpdate.CheckAsync(client, "1.3.1.01", CancellationToken.None, betas: true));
        var asset = await ReleaseUpdate.LatestAsync(client, CancellationToken.None, requireSetup: true, betas: true);
        Assert.NotNull(asset);
        Assert.True(asset.IsSetup);
        string path = await ReleaseUpdate.DownloadSetupAsync(client, asset, Path.Combine(fixture.PathFor("downloads"), "beta"), null, CancellationToken.None);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(ReleaseUpdate.RecentUrl, requested[0]);
        // Once installed, the beta's setup is housekeeping, while the release it leads up to is not.
        Assert.Equal(1, ReleaseUpdate.CleanupDownloads(fixture.PathFor("downloads"), "1.3.1.02-beta.1"));
    }

    [Fact]
    public void BetaUpdatesAreOffUnlessThisPcsConfigurationOptsIn()
    {
        using var fixture = new MedalFixture();
        var config = new MedalSync.Config.MedalSyncConfig(
            1, "0123456789abcdef0123456789abcdef", "12345",
            @"\\192.0.2.10\Data\AppData\MedalSync", @"\\192.0.2.10\Data\Media\Videos", @"M:\Clips", @"E:\MedalCache",
            @"%LOCALAPPDATA%\MedalSync\bin\rclone.exe",
            new MedalSync.Config.MountConfig(5, 20, 20, 300, 30, 1000, 2, 53684), 4, []);
        string path = fixture.PathFor("config.json");
        Assert.False(ReleaseUpdate.BetaUpdatesEnabled(path));
        File.WriteAllBytes(path, MedalSync.Util.Json.Serialize(config));
        Assert.DoesNotContain("betaUpdates", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(ReleaseUpdate.BetaUpdatesEnabled(path));
        File.WriteAllBytes(path, MedalSync.Util.Json.Serialize(config with { BetaUpdates = true }));
        Assert.Contains("\"betaUpdates\": true", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.True(ReleaseUpdate.BetaUpdatesEnabled(path));
        // An unreadable or invalid configuration means stable releases only.
        File.WriteAllText(path, "{ \"betaUpdates\": true");
        Assert.False(ReleaseUpdate.BetaUpdatesEnabled(path));
        File.WriteAllBytes(path, MedalSync.Util.Json.Serialize(config with { BetaUpdates = true, AccountId = "not numeric" }));
        Assert.False(ReleaseUpdate.BetaUpdatesEnabled(path));
    }

    [Theory]
    [InlineData("1.3.0.1")]
    [InlineData("1.3.0.001")]
    [InlineData("1.3.0.00.1")]
    [InlineData("1.3.0.0a")]
    [InlineData("01.3.0.00")]
    public void RefusesMalformedHotfixTags(string version) =>
        Assert.Null(ReleaseUpdate.AvailableVersion(Release(version), "0.2.0"));

    [Fact]
    public void FindsPublishedNativeSetupWithExpectedDigestAndRepositoryUrl()
    {
        string setup = Release().Replace("-win-x64.zip", "-setup-win-x64.exe");
        Assert.Equal("0.3.0", ReleaseUpdate.AvailableVersion(setup, "0.2.0"));
        Assert.Null(ReleaseUpdate.AvailableVersion(setup.Replace("github.com/Larson-Logan", "example.com/other"), "0.2.0"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PrivateRepositoryOrNetworkRefusalDoesNotBlockLaunch(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(status)));
        Assert.Null(await ReleaseUpdate.CheckAsync(client, "0.2.0", CancellationToken.None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"draft\":\"wrong type\"}")]
    public async Task MalformedResponsesDoNotBlockLaunch(string json)
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        Assert.Null(await ReleaseUpdate.CheckAsync(client, "0.2.0", CancellationToken.None));
    }

    [Fact]
    public async Task ChecksOnlyPublicEndpointWithNoAuthorizationOrLocalInformation()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(ReleaseUpdate.LatestUrl, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release()) };
        }));
        Assert.Equal("0.3.0", await ReleaseUpdate.CheckAsync(client, "0.2.0", CancellationToken.None));
    }

    [Fact]
    public async Task TimeoutAndConnectionFailureAreNonfatal()
    {
        using var canceled = new HttpClient(new Handler(_ => throw new TaskCanceledException()));
        using var offline = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        Assert.Null(await ReleaseUpdate.CheckAsync(canceled, "0.2.0", CancellationToken.None));
        Assert.Null(await ReleaseUpdate.CheckAsync(offline, "0.2.0", CancellationToken.None));
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string('x', 1024 * 1024 + 1)) }));
        Assert.Null(await ReleaseUpdate.CheckAsync(client, "0.2.0", CancellationToken.None));
    }

    [Fact]
    public void ManualDiscoveryRequiresSetupAndRejectsDuplicateOrUnboundedAssets()
    {
        Assert.Null(ReleaseUpdate.PublishedAsset(Release(), requireSetup: true));
        string setup = Release().Replace("-win-x64.zip", "-setup-win-x64.exe");
        Assert.True(ReleaseUpdate.PublishedAsset(setup, requireSetup: true)!.IsSetup);
        foreach (long size in new[] { 0L, -1, 500L * 1024 * 1024 + 1 })
            Assert.Null(ReleaseUpdate.PublishedAsset(setup.Replace("\"size\":1024", $"\"size\":{size}")));
        using var parsed = JsonDocument.Parse(setup);
        string asset = parsed.RootElement.GetProperty("assets")[0].GetRawText();
        Assert.Null(ReleaseUpdate.PublishedAsset(setup.Replace(asset, asset + "," + asset)));
    }

    private static ReleaseAsset SetupAsset(byte[] bytes) => new("0.5.0.01", "medal-sync-0.5.0.01-setup-win-x64.exe",
        "https://github.com/Larson-Logan/medal-sync/releases/download/v0.5.0.01/medal-sync-0.5.0.01-setup-win-x64.exe",
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength);

    [Fact]
    public async Task DownloadsOnlyExpectedSetupAndPublishesItAfterHashAndLengthVerification()
    {
        using var fixture = new MedalFixture();
        byte[] bytes = [1, 2, 3, 4];
        var asset = SetupAsset(bytes);
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(asset.Url, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var progress = new List<long>();
        string path = await ReleaseUpdate.DownloadSetupAsync(client, asset, fixture.PathFor("downloads"),
            (received, total) => { progress.Add(received); Assert.Equal(bytes.Length, total); }, CancellationToken.None);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(new long[] { 0, bytes.Length }, progress);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        await Assert.ThrowsAsync<IOException>(() => ReleaseUpdate.DownloadSetupAsync(client, asset, fixture.PathFor("downloads"), null, CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("declared-length")]
    [InlineData("http")]
    [InlineData("cancel")]
    public async Task FailedOrCanceledDownloadsLeaveNoExecutableOrPartialFile(string failure)
    {
        using var fixture = new MedalFixture();
        byte[] expected = [1, 2, 3, 4];
        var asset = SetupAsset(expected);
        byte[] received = failure switch { "hash" => [4, 3, 2, 1], "short" => [1], "long" => [1, 2, 3, 4, 5], _ => expected };
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(failure == "http" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        { Content = failure == "declared-length" ? new ByteArrayContent([1]) : new UnknownLengthContent(received) }));
        using var cancellation = new CancellationTokenSource();
        string directory = fixture.PathFor("downloads");
        await Assert.ThrowsAnyAsync<Exception>(() => ReleaseUpdate.DownloadSetupAsync(client, asset, directory,
            (count, _) => { if (failure == "cancel" && count > 0) cancellation.Cancel(); }, cancellation.Token));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task DownloadRejectsUnexpectedHostFilenameOrDigestBeforeAnyNetworkOrDiskWork()
    {
        using var fixture = new MedalFixture();
        var asset = SetupAsset([1]);
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Network must not be used.")));
        string directory = fixture.PathFor("downloads");
        foreach (var bad in new[] { asset with { Url = "https://example.com/setup.exe" }, asset with { Name = "../setup.exe" },
            asset with { Sha256 = "bad" }, asset with { Size = 0 }, asset with { Version = "../0.5.0.01" }, asset with { Version = "0.5.0.1" } })
            await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseUpdate.DownloadSetupAsync(client, bad, directory, null, CancellationToken.None));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void ReusesOnlyAnEarlierDownloadThatStillMatchesTheRelease()
    {
        using var fixture = new MedalFixture();
        byte[] bytes = [1, 2, 3, 4];
        var asset = SetupAsset(bytes);
        string root = fixture.PathFor("downloads");
        Assert.Null(ReleaseUpdate.FindVerifiedDownload(root, asset));
        Directory.CreateDirectory(Path.Combine(root, "tampered"));
        File.WriteAllBytes(Path.Combine(root, "tampered", asset.Name), [4, 3, 2, 1]);
        Directory.CreateDirectory(Path.Combine(root, "truncated"));
        File.WriteAllBytes(Path.Combine(root, "truncated", asset.Name), [1, 2]);
        Assert.Null(ReleaseUpdate.FindVerifiedDownload(root, asset));
        string good = Path.Combine(root, "good", asset.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(good)!);
        File.WriteAllBytes(good, bytes);
        Assert.Equal(good, ReleaseUpdate.FindVerifiedDownload(root, asset));
        Assert.Null(ReleaseUpdate.FindVerifiedDownload(root, asset with { Name = "medal-sync-0.5.0.01-win-x64.zip" }));
    }

    [Fact]
    public void CleanupRemovesOnlyFoldersHoldingAlreadyInstalledSetups()
    {
        using var fixture = new MedalFixture();
        string root = fixture.PathFor("downloads");
        string Folder(string name, params string[] files)
        {
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            foreach (string file in files) File.WriteAllBytes(Path.Combine(directory, file), [1]);
            return directory;
        }
        string older = Folder("older", "medal-sync-1.3.0-setup-win-x64.exe");
        string same = Folder("same", "medal-sync-1.3.1.00-setup-win-x64.exe");
        string newer = Folder("newer", "medal-sync-1.3.1.01-setup-win-x64.exe");
        string other = Folder("other", "medal-sync-1.3.0.01-setup-win-x64.exe", "notes.txt");
        string empty = Folder("empty");
        string nested = Folder("nested", "medal-sync-1.3.0.01-setup-win-x64.exe");
        Directory.CreateDirectory(Path.Combine(nested, "child"));
        Assert.Equal(3, ReleaseUpdate.CleanupDownloads(root, "1.3.1.00"));
        Assert.False(Directory.Exists(older));
        Assert.False(Directory.Exists(same));
        Assert.False(Directory.Exists(empty));
        Assert.True(File.Exists(Path.Combine(newer, "medal-sync-1.3.1.01-setup-win-x64.exe")));
        Assert.True(File.Exists(Path.Combine(other, "notes.txt")));
        Assert.True(Directory.Exists(nested));
        Assert.Equal(0, ReleaseUpdate.CleanupDownloads(fixture.PathFor("missing"), "1.3.1.00"));
        Assert.Equal(0, ReleaseUpdate.CleanupDownloads(root, "not a version"));
    }

    [Fact]
    public void CleanupKeepsTheReleaseAnInstalledBetaLeadsUpTo()
    {
        using var fixture = new MedalFixture();
        string root = fixture.PathFor("downloads");
        string Folder(string name, string file)
        {
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, file), [1]);
            return directory;
        }
        string older = Folder("older", "medal-sync-1.3.1.01-setup-win-x64.exe");
        string release = Folder("release", "medal-sync-1.3.1.02-setup-win-x64.exe");
        Assert.Equal(1, ReleaseUpdate.CleanupDownloads(root, "1.3.1.02-beta.2"));
        Assert.False(Directory.Exists(older));
        Assert.True(Directory.Exists(release));
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

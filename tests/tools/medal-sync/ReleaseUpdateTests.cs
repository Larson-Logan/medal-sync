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

    private static ReleaseAsset SetupAsset(byte[] bytes) => new("0.5.0", "medal-sync-0.5.0-setup-win-x64.exe",
        "https://github.com/Larson-Logan/medal-sync/releases/download/v0.5.0/medal-sync-0.5.0-setup-win-x64.exe",
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
            asset with { Sha256 = "bad" }, asset with { Size = 0 }, asset with { Version = "../0.5.0" } })
            await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseUpdate.DownloadSetupAsync(client, bad, directory, null, CancellationToken.None));
        Assert.False(Directory.Exists(directory));
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

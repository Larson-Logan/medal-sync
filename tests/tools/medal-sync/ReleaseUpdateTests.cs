using System.Net;
using System.Text.Json;
using LarsonServer.MedalSync.Platform;

namespace LarsonServer.MedalSync.Tests;

public sealed class ReleaseUpdateTests
{
    private static string Release(string version = "0.3.0", bool draft = false, bool prerelease = false, string? digest = null) =>
        JsonSerializer.Serialize(new
        {
            draft, prerelease, tag_name = $"v{version}",
            assets = new[] { new { name = $"medal-sync-{version}-win-x64.zip",
                browser_download_url = $"https://github.com/Larson-Logan/medal-sync/releases/download/v{version}/medal-sync-{version}-win-x64.zip",
                digest = digest ?? "sha256:" + new string('a', 64) } },
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

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

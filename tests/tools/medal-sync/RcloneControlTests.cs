using System.Net;
using System.Net.Sockets;
using System.Text;
using MedalSync.Config;
using MedalSync.Platform;

namespace MedalSync.Tests;

public sealed class RcloneControlTests
{
    [Theory]
    [InlineData("https://example.invalid/core/version")]
    [InlineData("//example.invalid/core/version")]
    [InlineData("../core/version")]
    [InlineData("/core/version")]
    [InlineData("core/version?redirect=1")]
    [InlineData("config/dump")]
    public void RefusesUnreviewedCommandsBeforeSendingCredentials(string command)
    {
        using var control = new RcloneControl(53682, "synthetic", "synthetic", new Handler(_ =>
            throw new InvalidOperationException("An unreviewed request reached the network.")));
        Assert.Null(control.Call(command));
    }

    [Theory]
    [InlineData("core/version")]
    [InlineData("core/quit")]
    [InlineData("core/bwlimit")]
    [InlineData("vfs/refresh")]
    [InlineData("vfs/stats")]
    [InlineData("vfs/queue")]
    public void SendsReviewedRequestsOnlyToAuthenticatedLoopback(string command)
    {
        using var control = new RcloneControl(53682, "synthetic", "synthetic", new Handler(request =>
        {
            Assert.Equal($"http://127.0.0.1:53682/{command}", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic:synthetic")), request.Headers.Authorization.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        Assert.Equal("{}", control.Call(command));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedResponsesAreUnknownEvenWithoutContentLength(bool unknownLength)
    {
        using var control = new RcloneControl(53682, "synthetic", "synthetic", new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = unknownLength ? new UnboundedContent() : new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]),
            }));
        Assert.Null(control.Call("vfs/queue"));
    }

    [Fact]
    public async Task RealClientRefusesRedirects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        int requests = 0;
        Task server = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(cancellation.Token);
                    await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    while (await reader.ReadLineAsync(cancellation.Token) is { Length: > 0 }) { }
                    string response = ++requests == 1
                        ? $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }, TestContext.Current.CancellationToken);
        try
        {
            using var control = new RcloneControl(port, "synthetic", "synthetic");
            Assert.Null(control.Call("core/version", timeout: TimeSpan.FromSeconds(3)));
            Assert.Equal(1, requests);
        }
        finally
        {
            cancellation.Cancel();
            await server;
        }
    }

    [Fact]
    public void TransportFailureKeepsUploadStateUnknown()
    {
        using var control = new RcloneControl(53682, "synthetic", "synthetic", new Handler(_ => throw new HttpRequestException("synthetic offline")));
        Assert.Null(control.Call("vfs/stats"));
    }

    private static readonly MountConfig LimitedMount = new(5, 20, 20, 300, 30, 1000, 2, 53682) { BandwidthLimitMiB = 5 };

    [Fact]
    public void UploadLimitSwitchesOnlyWhenTheRateChanges()
    {
        var bodies = new List<string>();
        using var control = new RcloneControl(53682, "synthetic", "synthetic", new Handler(request =>
        {
            Assert.Equal("http://127.0.0.1:53682/core/bwlimit", request.RequestUri!.AbsoluteUri);
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"rate\":\"ok\"}") };
        }));
        var limit = new MountUploadLimit(LimitedMount);
        limit.Started(forRecording: true);

        Assert.Null(limit.Apply(control, recording: true));
        Assert.Empty(bodies);
        Assert.Null(limit.Apply(control, recording: false));
        Assert.Null(limit.Apply(control, recording: false));
        Assert.Null(limit.Apply(control, recording: true));
        Assert.Equal(["{\"rate\":\"off\"}", "{\"rate\":\"5Mi:off\"}"], bodies);

        // Attaching to a drive started elsewhere: its rate is unknown, so it is set once.
        limit.Reset();
        Assert.Null(limit.Apply(control, recording: true));
        Assert.Null(limit.Apply(control, recording: true));
        Assert.Equal(3, bodies.Count);
    }

    [Fact]
    public void FailedUploadLimitSwitchIsReportedAndRetriedNeverThrown()
    {
        int calls = 0;
        bool fail = true;
        using var control = new RcloneControl(53682, "synthetic", "synthetic", new Handler(_ =>
        {
            calls++;
            return fail ? throw new HttpRequestException("Synthetic control failure.")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var limit = new MountUploadLimit(LimitedMount with { FinishBandwidthLimitMiB = 50 });
        limit.Started(forRecording: true);

        Assert.Contains("50 MiB/s for finishing", limit.Apply(control, recording: false), StringComparison.Ordinal);
        Assert.Contains("could not be set", limit.Apply(null, recording: false), StringComparison.Ordinal);
        fail = false;
        Assert.Null(limit.Apply(control, recording: false));
        Assert.Null(limit.Apply(control, recording: false));
        Assert.Equal(2, calls);

        fail = true;
        Assert.Contains("5 MiB/s for recording", limit.Apply(control, recording: true), StringComparison.Ordinal);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class UnboundedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[4 * 1024 * 1024 + 1]).AsTask();
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
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

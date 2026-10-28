using System.Net.Http.Headers;
using System.Text;

namespace MedalSync.Platform;

/// <summary>
/// rclone's remote-control API on loopback, with the per-session login.
/// Any failure returns null so callers treat the state as unknown.
/// </summary>
internal sealed class RcloneControl : IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http;

    public RcloneControl(int port, string user, string password, HttpMessageHandler? handler = null)
    {
        // Control requests, including their per-session login, must stay on
        // loopback even if Windows or the inherited environment has a proxy.
        _http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
        _http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _http.Timeout = TimeSpan.FromMinutes(15);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    public string? Call(string command, string body = "{}", TimeSpan? timeout = null)
    {
        // A caller must never turn the authenticated loopback client into an
        // arbitrary URL fetch or invoke an unrelated remote-control operation.
        if (command is not ("core/version" or "core/quit" or "vfs/refresh" or "vfs/stats" or "vfs/queue")) return null;
        try
        {
            using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, command) { Content = content };
            using var response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes) return null;
            response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancel.Token).GetAwaiter().GetResult();
            return response.Content.ReadAsStringAsync(cancel.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

using System.Net.Http.Headers;
using System.Text;

namespace MedalSync.Platform;

/// <summary>
/// rclone's remote-control API on loopback, with the per-session login.
/// Any failure returns null so callers treat the state as unknown.
/// </summary>
internal sealed class RcloneControl : IDisposable
{
    private readonly HttpClient _http;

    public RcloneControl(int port, string user, string password, HttpMessageHandler? handler = null)
    {
        // Control requests, including their per-session login, must stay on
        // loopback even if Windows or the inherited environment has a proxy.
        _http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false });
        _http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _http.Timeout = TimeSpan.FromMinutes(15);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    public string? Call(string command, string body = "{}", TimeSpan? timeout = null)
    {
        try
        {
            using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = _http.PostAsync(command, content, cancel.Token).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode ? response.Content.ReadAsStringAsync(cancel.Token).GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

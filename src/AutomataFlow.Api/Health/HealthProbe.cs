namespace AutomataFlow.Api.Health;

/// <summary>
/// The container health check of the release image, which has no shell or HTTP client tool: requests a local health
/// endpoint and exits 0 only for HTTP 200. Loopback URLs only, so the probe can never be turned into an outbound request.
/// </summary>
public static class HealthProbe
{
    public static async Task<int> RunAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != Uri.UriSchemeHttp)
        {
            await Console.Error.WriteLineAsync("healthcheck: only http://127.0.0.1 or http://localhost URLs are allowed.");
            return 2;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(uri);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}

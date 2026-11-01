namespace Oblachka;

// Только кэш DNS текущего ядра, не системный DNS Windows и не настройки браузера.
public static class CoreDnsCache
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
        { Timeout = TimeSpan.FromSeconds(2) };

    public static async Task<bool> FlushAsync(CoreStatsEntry entry, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{entry.Port}/cache/dns/flush");
            request.Headers.Authorization = new("Bearer", entry.Secret);
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return false; }
    }
}

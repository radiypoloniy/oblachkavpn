namespace Oblachka;

// Переключатель выхода ядра туннеля (SingBoxTunConfig.ServerSelector) через clash API на 127.0.0.1
// с секретом сессии. Смена сервера под защитой не пересоздаёт туннель: меняется только выход,
// соединения на прежний сервер рвутся (interrupt_exist_connections), новые идут на выбранный.
public static class CoreSelector
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
        { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<bool> SelectAsync(CoreStatsEntry entry, string outbound, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{entry.Port}/proxies/{SingBoxTunConfig.ServerSelector}")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { name = outbound }), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new("Bearer", entry.Secret);
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }
}

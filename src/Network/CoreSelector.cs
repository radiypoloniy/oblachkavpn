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

    // Запрос к адресу через текущий выход переключателя — силами самого ядра туннеля. Время в мс или null.
    public static async Task<long?> DelayAsync(CoreStatsEntry entry, Uri target, CancellationToken token)
    {
        try
        {
            var url = $"http://127.0.0.1:{entry.Port}/proxies/{SingBoxTunConfig.ServerSelector}/delay?timeout=10000&url={Uri.EscapeDataString(target.AbsoluteUri)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", entry.Secret);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var response = await Delays.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            // Ошибка — не 200; у ближнего адреса задержка бывает 0 мс.
            return body?["delay"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<long>(out var ms) && ms >= 0 ? Math.Max(1, ms) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            return null;
        }
    }

    private static readonly HttpClient Delays = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
        { Timeout = Timeout.InfiniteTimeSpan };
}

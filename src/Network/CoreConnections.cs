using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Oblachka;

// Соединения ядра туннеля из его локального интерфейса (clash API sing-box, CoreStatsEntry):
// программа, адрес и выход, через который соединение ушло на самом деле. В «Весь компьютер»
// таблицы Windows этого не показывают: программа всегда подключается к адаптеру туннеля, а
// напрямую её отправляет уже ядро — по правилу программы или сайта.
public sealed record CoreConnection(string? ProcessPath, bool Udp, string? Host, string? Address, int Port, IReadOnlyList<string> Chains);

public static class CoreConnections
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
        { Timeout = TimeSpan.FromSeconds(2) };

    // null — интерфейс не ответил (старая служба, ядро перезапускается): окно возьмёт таблицы Windows.
    public static async Task<IReadOnlyList<CoreConnection>?> ReadAsync(CoreStatsEntry entry, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{entry.Port}/connections");
            request.Headers.Authorization = new("Bearer", entry.Secret);
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    internal static IReadOnlyList<CoreConnection> Parse(string json)
    {
        var result = new List<CoreConnection>();
        if (JsonNode.Parse(json)?["connections"] is not JsonArray connections) return result;
        foreach (var node in connections.OfType<JsonObject>())
        {
            var meta = node["metadata"] as JsonObject;
            var chains = (node["chains"] as JsonArray)?.Select(c => c?.GetValue<string>()).OfType<string>().ToArray() ?? [];
            int.TryParse(meta?["destinationPort"]?.ToString(), out var port);
            result.Add(new(Text(meta?["processPath"]), string.Equals(Text(meta?["network"]), "udp", StringComparison.OrdinalIgnoreCase),
                Text(meta?["host"]), Text(meta?["destinationIP"]), port, chains));
        }
        return result;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}

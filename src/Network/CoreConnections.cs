using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Oblachka;

// Соединения ядра туннеля из его локального интерфейса (clash API sing-box, CoreStatsEntry):
// программа, адрес и выход, через который соединение ушло на самом деле. В «Весь компьютер»
// таблицы Windows этого не показывают: программа всегда подключается к адаптеру туннеля, а
// напрямую её отправляет уже ядро — по правилу программы или сайта.
// Upload и Download — байты с начала соединения: из разницы между опросами окно считает скорость.
public sealed record CoreConnection(string? ProcessPath, bool Udp, string? Host, string? Address, int Port, IReadOnlyList<string> Chains, string? Id = null,
    long Upload = 0, long Download = 0)
{
    public bool Direct => Chains.Contains(SingBoxTunConfig.UserDirect);
}

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

    // Закрыть соединения программ, чьё правило изменилось: ядро рвёт их и со стороны туннеля, так
    // что программа переподключается уже по новому правилу. В отличие от сброса TCP службой
    // (ConnectionReset), так закрываются и UDP (голос, игры, Steam), и IPv6.
    public static Task<int> CloseChangedAsync(CoreStatsEntry entry, IReadOnlyList<ProgramRule> before, IReadOnlyList<ProgramRule> after, CancellationToken token) =>
        CloseAsync(entry, c => c.ProcessPath is { } path && ConnectionReset.Changed(before, after, path), token);

    // Второй проход после смены правил: закрыть только соединения, что всё ещё идут не туда, куда
    // велит новое правило. Первый проход мог успеть раньше, чем ядро перечитало правила, а свежие
    // соединения, открытые уже по новому правилу, трогать незачем.
    public static Task<int> CloseMismatchedAsync(CoreStatsEntry entry, IReadOnlyList<ProgramRule> before, IReadOnlyList<ProgramRule> after, CancellationToken token) =>
        CloseAsync(entry, c => c.ProcessPath is { } path && ConnectionReset.Changed(before, after, path) && ProgramRoutingPlan.Find(after, path) is { } rule &&
            (rule.Action == SiteAction.Block || (rule.Action == SiteAction.Direct) != c.Direct), token);

    // Закрыть выбранные соединения: «Закрыть» у сайта или программы в «Сейчас в сети».
    public static Task<int> CloseIdsAsync(CoreStatsEntry entry, IReadOnlyCollection<string> ids, CancellationToken token) =>
        CloseAsync(entry, c => c.Id is { } id && ids.Contains(id), token);

    // Закрыть соединения к сайтам, чей путь сменился (сайты zapret: напрямую ↔ VPN). Имя сайта ядро
    // знает из TLS/QUIC (sniff), поэтому закрываются и соединения, открытые по адресу.
    public static Task<int> CloseHostsAsync(CoreStatsEntry entry, Func<string, bool> host, CancellationToken token) =>
        CloseAsync(entry, c => c.Host is { } name && host(name), token);

    private static async Task<int> CloseAsync(CoreStatsEntry entry, Func<CoreConnection, bool> match, CancellationToken token)
    {
        var connections = await ReadAsync(entry, token).ConfigureAwait(false);
        if (connections is null) return 0;
        var closed = 0;
        foreach (var connection in connections)
        {
            if (connection.Id is not { } id || !match(connection)) continue;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:{entry.Port}/connections/{Uri.EscapeDataString(id)}");
                request.Headers.Authorization = new("Bearer", entry.Secret);
                using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) closed++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }
        return closed;
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
                Text(meta?["host"]), Text(meta?["destinationIP"]), port, chains, Text(node["id"]), Bytes(node["upload"]), Bytes(node["download"])));
        }
        return result;
    }

    private static long Bytes(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var n) && n > 0 ? n : 0;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}

using System.Text.Json.Nodes;

namespace Oblachka;

// Ядро окна под «Весь компьютер» выходит к серверу через физический интерфейс, а не через туннель.
// Адреса узлов из маршрутов больше не вырезаются (SingBoxTunConfig): без привязки соединение ядра
// с сервером ушло бы в туннель — по кругу. Так ядро туннеля поступает со своими выходами давно.
//   sing-box: auto_detect_interface — интерфейс выбирает и при смене сети меняет само ядро;
//             локальный вход ядра туннеля (127.0.0.1) оно привязывает к loopback.
//   Xray:     sockopt.interface у выхода, который открывает соединение с сервером: у «Обхода DPI»
//             это freedom из dialerProxy (TlsFragment). Сеть сменилась — ядро перезапускают
//             (SystemTunnelSession.FollowNetworkAsync).
public static class PhysicalBinding
{
    // Меняет готовую конфигурацию на месте. Возвращает имя интерфейса, к которому привязан Xray,
    // или null — sing-box следит за сетью сам.
    public static string? Apply(JsonObject config, CoreKind core, Func<string?>? physical = null)
    {
        if (core == CoreKind.SingBox)
        {
            ((JsonObject)config["route"]!)["auto_detect_interface"] = true;
            return null;
        }
        // Без сети привязать не к чему, а без привязки ядро гоняло бы свой трафик через себя же.
        var name = (physical ?? PhysicalNetwork.DefaultInterface)() ?? throw new UserError("Нет подключения к сети: ядру не через что выйти к серверу.");
        var outbounds = ((JsonArray)config["outbounds"]!).OfType<JsonObject>().ToArray();
        var tunnel = outbounds.FirstOrDefault(o => Text(o["tag"]) == "tunnel") ?? throw new UserError("В конфигурации ядра нет выхода к серверу.");
        // Цепочка dialerProxy ведёт к выходу, который сам открывает сокет.
        var dialer = tunnel;
        for (var hops = 0; Text(dialer["streamSettings"]?["sockopt"]?["dialerProxy"]) is { } next; hops++)
            dialer = hops < outbounds.Length ? outbounds.FirstOrDefault(o => Text(o["tag"]) == next) ?? throw new UserError("В конфигурации ядра оборвана цепочка выходов.")
                : throw new UserError("В конфигурации ядра цепочка выходов замкнута.");
        var stream = dialer["streamSettings"] as JsonObject ?? new JsonObject();
        dialer["streamSettings"] = stream;
        var sockopt = stream["sockopt"] as JsonObject ?? new JsonObject();
        stream["sockopt"] = sockopt;
        sockopt["interface"] = name;
        return name;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

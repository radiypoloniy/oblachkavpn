using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class DirectOutboundPolicy
{
    // Rebuild a bounded native Hysteria2 outbound. Unknown options retain the
    // existing separate-core path; never remove TLS pins or port-hopping settings.
    private static void Hysteria(JsonObject outbound, JsonObject result)
    {
        if (outbound["tls"] is not JsonObject tls) throw new UserError("Выходу Hysteria2 нужен TLS.");
        Only(tls, "enabled", "server_name", "alpn");
        if (tls["alpn"] is not null && tls["alpn"] is not JsonArray) throw new UserError("Неверный ALPN выхода.");
        foreach (var key in new[] { "up_mbps", "down_mbps" })
        {
            if (outbound[key] is null) continue;
            if (outbound[key] is not JsonValue value || !value.TryGetValue<int>(out var rate) || rate is < 0 or > 1_000_000)
                throw new UserError("Неверная скорость Hysteria2.");
            result[key] = rate;
        }
        if (outbound["network"] is not null)
        {
            var network = Text(outbound, "network");
            if (network is not ("tcp" or "udp")) throw new UserError("Неверный протокол сети Hysteria2.");
            result["network"] = network;
        }
        if (outbound["obfs"] is not null)
        {
            if (outbound["obfs"] is not JsonObject obfs) throw new UserError("Неверная обфускация Hysteria2.");
            Only(obfs, "type", "password");
            if (Text(obfs, "type") != "salamander") throw new UserError("Неподдерживаемая обфускация Hysteria2.");
            result["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = Secret(obfs, "password") };
        }
    }
}

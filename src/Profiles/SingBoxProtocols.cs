using System.Text.Json.Nodes;

namespace Oblachka;

// Поля и проверки каждого протокола sing-box. Возвращают подпись транспорта для списка
// («VMESS · WS», «TUIC / QUIC»). Общие поля, адрес и TLS проверяет SingBoxProfiles.
internal static class SingBoxProtocols
{
    public static string[] Fields(string type) => type switch
    {
        "vless" => new[] { "uuid", "flow", "transport", "packet_encoding" },
        "trojan" => new[] { "password", "transport" },
        "vmess" => new[] { "uuid", "security", "alter_id", "global_padding", "authenticated_length", "transport", "packet_encoding" },
        "shadowsocks" => new[] { "method", "password", "udp_over_tcp" },
        "tuic" => new[] { "uuid", "password", "congestion_control", "udp_relay_mode", "udp_over_stream", "zero_rtt_handshake", "heartbeat" },
        "anytls" => new[] { "password", "idle_session_check_interval", "idle_session_timeout", "min_idle_session" },
        _ => new[] { "password", "up_mbps", "down_mbps", "obfs", "server_ports", "hop_interval", "hop_interval_max" }
    };

    public static string Check(JsonObject outbound, string type)
    {
        switch (type)
        {
            case "vless":
                if (!Guid.TryParse(outbound["uuid"]?.GetValue<string>(), out _)) throw new UserError("sing-box: нужен UUID VLESS.");
                return Transport(outbound, "");
            case "trojan":
                Password(outbound, "Trojan");
                return Transport(outbound, "TROJAN · ");
            case "vmess":
                if (!Guid.TryParse(outbound["uuid"]?.GetValue<string>(), out _)) throw new UserError("sing-box: нужен UUID VMess.");
                XrayProtocols.CheckVmess(outbound["security"]?.GetValue<string>(), outbound["alter_id"]?.GetValue<int>() ?? 0);
                return Transport(outbound, "VMESS · ");
            case "shadowsocks":
                XrayProtocols.CheckShadowsocks(outbound["method"]?.GetValue<string>(), outbound["password"]?.GetValue<string>());
                return "SHADOWSOCKS · " + outbound["method"]!.GetValue<string>().ToUpperInvariant();
            case "tuic":
                if (!Guid.TryParse(outbound["uuid"]?.GetValue<string>(), out _)) throw new UserError("sing-box: нужен UUID TUIC.");
                if (outbound["udp_relay_mode"]?.GetValue<string>() is { } relay && relay is not ("native" or "quic"))
                    throw new UserError("sing-box: неизвестный режим UDP у TUIC.");
                if (outbound["congestion_control"]?.GetValue<string>() is { } cc && cc is not ("cubic" or "new_reno" or "bbr"))
                    throw new UserError("sing-box: неизвестный алгоритм congestion_control у TUIC.");
                return "TUIC / QUIC";
            case "anytls":
                Password(outbound, "AnyTLS");
                return "ANYTLS";
            default:
                Password(outbound, "Hysteria2");
                if (outbound["obfs"] is JsonObject obfs)
                {
                    SingBoxProfiles.AllowOnly(obfs, "type", "password");
                    if (obfs["type"]?.GetValue<string>() != "salamander" || string.IsNullOrWhiteSpace(obfs["password"]?.GetValue<string>()))
                        throw new UserError("sing-box: в этой сборке поддерживается obfs salamander с паролем.");
                }
                return "HYSTERIA2 / QUIC";
        }
    }

    // WireGuard (endpoint): адрес в peers, свои ключи. Возвращает подпись.
    public static string WireGuard(JsonObject endpoint)
    {
        SingBoxProfiles.AllowOnly(endpoint, "type", "tag", "mtu", "address", "private_key", "peers", "udp_timeout", "workers", "system", "name");
        if (endpoint["system"]?.GetValue<bool>() == true) throw new UserError("WireGuard: системный интерфейс не поддерживается, только встроенный.");
        Key(endpoint["private_key"], "закрытый ключ");
        if (endpoint["address"] is not JsonArray addresses || addresses.Count is 0 or > 4 ||
            addresses.Any(a => a?.GetValue<string>() is not { } cidr || !System.Net.IPNetwork.TryParse(cidr, out _)))
            throw new UserError("WireGuard: нужен адрес интерфейса (Address), например 10.0.0.2/32.");
        if ((endpoint["mtu"]?.GetValue<int>() ?? 1408) is < 1280 or > 1500) throw new UserError("WireGuard: MTU должен быть от 1280 до 1500.");
        if (endpoint["peers"] is not JsonArray peers || peers.Count != 1 || peers[0] is not JsonObject peer)
            throw new UserError("WireGuard: поддерживается ровно один сервер (Peer).");
        SingBoxProfiles.AllowOnly(peer, "address", "port", "public_key", "pre_shared_key", "allowed_ips", "persistent_keepalive_interval", "reserved");
        var address = peer["address"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace)) throw new UserError("WireGuard: нужен адрес сервера (Endpoint).");
        if ((peer["port"]?.GetValue<int>() ?? 0) is < 1 or > 65535) throw new UserError("WireGuard: порт сервера должен быть от 1 до 65535.");
        Key(peer["public_key"], "открытый ключ сервера");
        if (peer["pre_shared_key"] is { } psk) Key(psk, "общий ключ");
        if (peer["reserved"] is JsonArray reserved && (reserved.Count != 3 || reserved.Any(b => b?.GetValue<int>() is not (>= 0 and <= 255))))
            throw new UserError("WireGuard: reserved — три числа от 0 до 255.");
        return "WIREGUARD / UDP";
    }

    private static void Key(JsonNode? node, string what)
    {
        var text = node?.GetValue<string>() ?? "";
        Span<byte> bytes = stackalloc byte[33];
        if (!Convert.TryFromBase64String(text, bytes, out var length) || length != 32) throw new UserError("WireGuard: неверный " + what + ".");
    }

    private static void Password(JsonObject outbound, string protocol)
    {
        if (string.IsNullOrWhiteSpace(outbound["password"]?.GetValue<string>())) throw new UserError("sing-box: нужен пароль " + protocol + ".");
    }

    private static string Transport(JsonObject outbound, string prefix)
    {
        if (outbound["transport"] is not JsonObject options) return prefix + "TCP";
        var transportType = options["type"]?.GetValue<string>();
        var fields = transportType switch
        {
            "ws" => new[] { "type", "path", "headers", "max_early_data", "early_data_header_name" },
            "grpc" => new[] { "type", "service_name", "idle_timeout", "ping_timeout", "permit_without_stream" },
            "httpupgrade" => new[] { "type", "host", "path", "headers" },
            "http" => new[] { "type", "host", "path", "method", "headers", "idle_timeout", "ping_timeout" },
            _ => throw new UserError("sing-box: этот транспорт не поддерживается. XHTTP импортируйте как Xray JSON.")
        };
        SingBoxProfiles.AllowOnly(options, fields);
        return prefix + transportType!.ToUpperInvariant();
    }
}

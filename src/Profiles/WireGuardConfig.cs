using System.Text.Json.Nodes;

namespace Oblachka;

// WireGuard: обычный файл .conf ([Interface] / [Peer]) и общая сборка профиля для ссылок.
// Профиль — endpoint sing-box. Поддерживается один сервер; DNS из файла не применяется:
// его задаёт сам клиент. Конфигурация AmneziaWG (Jc, S1, H1 … в [Interface]) — профиль выхода
// amneziawg своего ядра туннеля; AllowedIPs у неё не применяются — через туннель идёт всё.
public static class WireGuardConfig
{
    public static bool Looks(string text) =>
        text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) && text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);

    public static Profile Parse(string text, string name = "")
    {
        var sections = new List<(string Name, Dictionary<string, string> Values)>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { sections.Add((line[1..^1].Trim().ToLowerInvariant(), new(StringComparer.OrdinalIgnoreCase))); continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0 || sections.Count == 0) throw new UserError("Не удалось прочитать конфигурацию WireGuard.");
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            // Повтор Address/AllowedIPs допустим и складывается через запятую.
            sections[^1].Values[key] = sections[^1].Values.TryGetValue(key, out var old) ? old + "," + value : value;
        }
        var face = sections.Where(s => s.Name == "interface").Select(s => s.Values).ToArray();
        var peers = sections.Where(s => s.Name == "peer").Select(s => s.Values).ToArray();
        if (face.Length != 1) throw new UserError("В конфигурации WireGuard нужен один раздел [Interface].");
        if (peers.Length != 1) throw new UserError("Поддерживается конфигурация WireGuard с одним сервером ([Peer]).");
        if (peers[0].Keys.Any(k => SingBoxProtocols.AmneziaKeys.Contains(k.ToLowerInvariant())))
            throw new UserError("AmneziaWG: параметры Jc, S1, H1 … должны быть в разделе [Interface].");
        string F(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : "";
        var endpoint = F(peers[0], "Endpoint");
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out var port)) throw new UserError("WireGuard: нужен Endpoint вида адрес:порт.");
        var host = endpoint[..colon].Trim('[', ']');
        var amnezia = face[0].Where(p => SingBoxProtocols.AmneziaKeys.Contains(p.Key.ToLowerInvariant())).ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value);
        if (amnezia.Count > 0)
            return Amnezia(F(face[0], "PrivateKey"), F(face[0], "Address"), F(face[0], "MTU"), host, port, F(peers[0], "PublicKey"),
                F(peers[0], "PresharedKey"), F(peers[0], "PersistentKeepalive"), amnezia, name.Length > 0 ? name : "AmneziaWG · " + host);
        return Build(F(face[0], "PrivateKey"), F(face[0], "Address"), F(face[0], "MTU"), host, port, F(peers[0], "PublicKey"),
            F(peers[0], "PresharedKey"), "", F(peers[0], "PersistentKeepalive"), name.Length > 0 ? name : "WireGuard · " + host, F(peers[0], "AllowedIPs"));
    }

    public static Profile Amnezia(string privateKey, string addresses, string mtu, string host, int port, string publicKey, string preshared,
        string keepalive, IReadOnlyDictionary<string, string> obfuscation, string name)
    {
        var outbound = new JsonObject
        {
            ["type"] = "amneziawg", ["server"] = host, ["server_port"] = port, ["private_key"] = privateKey, ["peer_public_key"] = publicKey,
            ["local_address"] = new JsonArray(addresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["mtu"] = int.TryParse(mtu, out var size) ? size : 1280,
            ["obfuscation"] = new JsonObject(obfuscation.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value.Trim())))
        };
        if (preshared.Length > 0) outbound["pre_shared_key"] = preshared;
        if (int.TryParse(keepalive, out var interval) && interval > 0) outbound["persistent_keepalive_interval"] = interval;
        return SingBoxProfiles.Parse(outbound, name);
    }

    public static Profile Build(string privateKey, string addresses, string mtu, string host, int port, string publicKey, string preshared,
        string reserved, string keepalive, string name, string allowedIps = "")
    {
        static JsonArray List(string text) =>
            new(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        var peer = new JsonObject
        {
            ["address"] = host, ["port"] = port, ["public_key"] = publicKey,
            ["allowed_ips"] = allowedIps.Length > 0 ? List(allowedIps) : new JsonArray("0.0.0.0/0", "::/0")
        };
        if (preshared.Length > 0) peer["pre_shared_key"] = preshared;
        if (int.TryParse(keepalive, out var interval) && interval is > 0 and <= 3600) peer["persistent_keepalive_interval"] = interval;
        if (reserved.Length > 0)
        {
            var bytes = reserved.Split(',', StringSplitOptions.TrimEntries);
            if (bytes.Length != 3 || bytes.Any(b => !int.TryParse(b, out _))) throw new UserError("WireGuard: reserved — три числа от 0 до 255.");
            peer["reserved"] = new JsonArray(bytes.Select(b => (JsonNode?)JsonValue.Create(int.Parse(b))).ToArray());
        }
        var endpoint = new JsonObject
        {
            ["type"] = "wireguard", ["address"] = List(addresses), ["private_key"] = privateKey,
            ["mtu"] = int.TryParse(mtu, out var size) ? size : 1408, ["peers"] = new JsonArray(peer)
        };
        return SingBoxProfiles.Parse(endpoint, name);
    }
}

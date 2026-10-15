using System.Text.Json.Nodes;

namespace Oblachka;

// WireGuard: обычный файл .conf ([Interface] / [Peer]) и общая сборка профиля для ссылок.
// Профиль — endpoint sing-box. Поддерживается один сервер; DNS из файла не применяется:
// его задаёт сам клиент. Конфигурации AmneziaWG (Jc, S1, H1 …) отклоняются явно.
public static class WireGuardConfig
{
    private static readonly string[] AmneziaKeys = { "jc", "jmin", "jmax", "s1", "s2", "s3", "s4", "h1", "h2", "h3", "h4", "i1", "i2", "i3", "i4", "i5" };

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
        if (face[0].Keys.Concat(peers[0].Keys).Any(k => AmneziaKeys.Contains(k.ToLowerInvariant())))
            throw new UserError("Это конфигурация AmneziaWG. Её поддержка пока не готова — нужна конфигурация обычного WireGuard.");
        string F(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : "";
        var endpoint = F(peers[0], "Endpoint");
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out var port)) throw new UserError("WireGuard: нужен Endpoint вида адрес:порт.");
        var host = endpoint[..colon].Trim('[', ']');
        return Build(F(face[0], "PrivateKey"), F(face[0], "Address"), F(face[0], "MTU"), host, port, F(peers[0], "PublicKey"),
            F(peers[0], "PresharedKey"), "", F(peers[0], "PersistentKeepalive"), name.Length > 0 ? name : "WireGuard · " + host, F(peers[0], "AllowedIPs"));
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

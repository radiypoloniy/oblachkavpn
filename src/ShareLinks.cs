using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot;

// Share links: vless://, trojan://, hysteria2:// (hy2://). Unknown parameters fail with their names only, never values.
public static class ShareLinks
{
    private static readonly string[] Schemes = { "vless://", "trojan://", "hysteria2://", "hy2://" };
    private static readonly string[] StreamKeys = { "type", "security", "sni", "fp", "pbk", "sid", "spx", "pqv", "alpn", "ech", "allowInsecure", "insecure", "host", "path", "mode", "extra", "serviceName", "authority", "headerType" };

    public static bool IsLink(string line) => Schemes.Any(s => line.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    public static Profile Parse(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new UserError("Не удалось прочитать ссылку подключения.");
        var q = Query(uri);
        var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        return uri.Scheme.ToLowerInvariant() switch
        {
            "vless" => Vless(uri, q, name),
            "trojan" => Trojan(uri, q, name),
            "hysteria2" or "hy2" => Hysteria2(uri, q, name),
            _ => throw new UserError("Этот тип ссылки пока не поддерживается.")
        };
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            if (!q.TryAdd(key, pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : ""))
                throw new UserError("В ссылке повторяется параметр " + SafeKey(key) + ". Экспортируй профиль как JSON.");
        }
        return q;
    }

    private static void RejectUnknown(Dictionary<string, string> q, IEnumerable<string> known)
    {
        var set = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var unknown = q.Keys.Where(k => !set.Contains(k)).Select(SafeKey).Take(5).ToArray();
        if (unknown.Length > 0)
            throw new UserError("Ссылка содержит незнакомые параметры: " + string.Join(", ", unknown) + ". Импортируй клиентский JSON или сообщи разработчику эти названия.");
    }

    // Names only: values may carry keys or passwords.
    private static string SafeKey(string key) => new(key.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(24).ToArray());

    private static Profile Vless(Uri uri, Dictionary<string, string> q, string name)
    {
        RejectUnknown(q, StreamKeys.Concat(new[] { "encryption", "flow", "packetEncoding" }));
        if (q.ContainsKey("packetEncoding")) throw new UserError("Параметр packetEncoding требует импорта через Xray JSON.");
        var id = Uri.UnescapeDataString(uri.UserInfo);
        if (!Guid.TryParse(id, out _)) throw new UserError("В ссылке нужен UUID пользователя VLESS.");
        var outbound = new JsonObject
        {
            ["protocol"] = "vless",
            ["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = uri.Host, ["port"] = uri.Port > 0 ? uri.Port : 443,
                ["users"] = new JsonArray(new JsonObject { ["id"] = id, ["encryption"] = Get(q, "encryption", "none"), ["flow"] = Get(q, "flow") }) }) },
            ["streamSettings"] = Stream(uri, q, "none")
        };
        return Profiles.FromOutbound(outbound, name);
    }

    private static Profile Trojan(Uri uri, Dictionary<string, string> q, string name)
    {
        RejectUnknown(q, StreamKeys);
        var password = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrEmpty(password)) throw new UserError("В Trojan-ссылке нет пароля.");
        var outbound = new JsonObject
        {
            ["protocol"] = "trojan",
            ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = uri.Host, ["port"] = uri.Port > 0 ? uri.Port : 443, ["password"] = password }) },
            // Trojan links default to TLS, unlike VLESS.
            ["streamSettings"] = Stream(uri, q, "tls")
        };
        return Profiles.FromOutbound(outbound, name);
    }

    private static JsonObject Stream(Uri uri, Dictionary<string, string> q, string defaultSecurity)
    {
        if (Get(q, "allowInsecure") is "1" or "true" || Get(q, "insecure") is "1" or "true") throw new UserError("Ссылка отключает проверку TLS-сертификата.");
        if (Get(q, "headerType", "none") != "none") throw new UserError("Параметр headerType требует импорта через Xray JSON.");
        var network = Get(q, "type", "tcp").ToLowerInvariant();
        if (network == "raw") network = "tcp";
        var security = Get(q, "security", defaultSecurity).ToLowerInvariant();
        var stream = new JsonObject { ["network"] = network, ["security"] = security };
        if (security == "reality")
        {
            if (string.IsNullOrWhiteSpace(Get(q, "pbk")) || string.IsNullOrWhiteSpace(Get(q, "sni")))
                throw new UserError("В REALITY-ссылке нужны pbk и sni.");
            var reality = new JsonObject { ["serverName"] = Get(q, "sni"), ["fingerprint"] = Get(q, "fp", "chrome"), ["publicKey"] = Get(q, "pbk"), ["shortId"] = Get(q, "sid"), ["spiderX"] = Get(q, "spx", "/") };
            if (!string.IsNullOrEmpty(Get(q, "pqv"))) reality["mldsa65Verify"] = Get(q, "pqv");
            stream["realitySettings"] = reality;
        }
        else if (security == "tls")
        {
            var tls = new JsonObject { ["serverName"] = Get(q, "sni", uri.Host), ["allowInsecure"] = false };
            if (q.ContainsKey("fp")) tls["fingerprint"] = Get(q, "fp");
            if (q.ContainsKey("alpn")) tls["alpn"] = new JsonArray(Get(q, "alpn").Split(',').Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            if (!string.IsNullOrEmpty(Get(q, "ech"))) tls["echConfigList"] = Get(q, "ech");
            stream["tlsSettings"] = tls;
        }
        switch (network)
        {
            case "xhttp":
                var xhttp = new JsonObject { ["path"] = Get(q, "path", "/"), ["host"] = Get(q, "host"), ["mode"] = Get(q, "mode", "auto") };
                if (q.ContainsKey("extra"))
                {
                    try { xhttp["extra"] = JsonNode.Parse(Get(q, "extra")) as JsonObject ?? throw new UserError("extra должен быть JSON-объектом."); }
                    catch (JsonException) { throw new UserError("Не удалось прочитать XHTTP extra. Импортируй Xray JSON."); }
                }
                stream["xhttpSettings"] = xhttp;
                break;
            case "ws":
                var ws = new JsonObject { ["path"] = Get(q, "path", "/") };
                if (q.ContainsKey("host")) ws["headers"] = new JsonObject { ["Host"] = Get(q, "host") };
                stream["wsSettings"] = ws;
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = Get(q, "serviceName"), ["authority"] = Get(q, "authority") };
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new JsonObject { ["path"] = Get(q, "path", "/"), ["host"] = Get(q, "host") };
                break;
        }
        return stream;
    }

    private static Profile Hysteria2(Uri uri, Dictionary<string, string> q, string name)
    {
        RejectUnknown(q, new[] { "sni", "obfs", "obfs-password", "insecure", "mport", "alpn", "pinSHA256" });
        if (Get(q, "insecure") is "1" or "true") throw new UserError("Ссылка отключает проверку TLS-сертификата.");
        if (q.ContainsKey("pinSHA256")) throw new UserError("Hysteria2 pinSHA256 пока не поддерживается: используйте сертификат с обычной проверкой.");
        var password = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrEmpty(password)) throw new UserError("В Hysteria2-ссылке нет пароля.");
        var tls = new JsonObject { ["enabled"] = true, ["server_name"] = Get(q, "sni", uri.Host) };
        tls["alpn"] = new JsonArray((q.ContainsKey("alpn") ? Get(q, "alpn").Split(',') : new[] { "h3" }).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2", ["server"] = uri.Host, ["server_port"] = uri.Port > 0 ? uri.Port : 443,
            ["password"] = password, ["tls"] = tls
        };
        if (q.ContainsKey("obfs"))
        {
            if (!Get(q, "obfs").Equals("salamander", StringComparison.OrdinalIgnoreCase)) throw new UserError("Поддерживается только obfs salamander.");
            outbound["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = Get(q, "obfs-password") };
        }
        if (!string.IsNullOrWhiteSpace(Get(q, "mport"))) outbound["server_ports"] = PortRanges(Get(q, "mport"));
        return SingBoxProfiles.Parse(outbound, name);
    }

    internal static JsonArray PortRanges(string mport)
    {
        var result = new JsonArray();
        foreach (var part in mport.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', 2);
            if (!int.TryParse(bounds[0], out var from) || from is < 1 or > 65535) throw new UserError("Неверный диапазон портов Hysteria2.");
            var to = from;
            if (bounds.Length == 2 && (!int.TryParse(bounds[1], out to) || to < from || to > 65535)) throw new UserError("Неверный диапазон портов Hysteria2.");
            result.Add(from + ":" + to);
        }
        if (result.Count is 0 or > 16) throw new UserError("Неверный диапазон портов Hysteria2.");
        return result;
    }

    private static string Get(Dictionary<string, string> q, string key, string fallback = "") => q.TryGetValue(key, out var value) ? value : fallback;
}

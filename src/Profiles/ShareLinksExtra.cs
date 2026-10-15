using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

// Ссылки ss://, vmess://, tuic://, anytls://, wireguard:// (wg://) → нативные профили sing-box.
// Все проверки шифрования и полей — в SingBoxProfiles: ссылка только переводится в его формат.
public static partial class ShareLinks
{
    // ss://BASE64(method:password)@host:port#имя (SIP002), ss://method:password@… (2022, без base64)
    // и старый вид ss://BASE64(method:password@host:port)#имя.
    private static Profile Shadowsocks(string input)
    {
        var (body, name) = SplitFragment(input["ss://".Length..]);
        var query = "";
        var at = body.IndexOf('?');
        if (at >= 0) { query = body[at..]; body = body[..at]; }
        body = body.TrimEnd('/');
        if (!body.Contains('@')) body = Base64Text(body) ?? throw new UserError("Не удалось прочитать ссылку Shadowsocks.");
        var split = body.LastIndexOf('@');
        var credentials = body[..split];
        if (!credentials.Contains(':')) credentials = Base64Text(Uri.UnescapeDataString(credentials)) ?? throw new UserError("Не удалось прочитать ссылку Shadowsocks.");
        else credentials = Uri.UnescapeDataString(credentials);
        var colon = credentials.IndexOf(':');
        if (colon <= 0) throw new UserError("В ссылке Shadowsocks нет метода шифрования или пароля.");
        var uri = new Uri("ss://x@" + body[(split + 1)..] + query);
        var q = Query(uri);
        RejectUnknown(q, new[] { "plugin", "outline", "type" });
        if (!string.IsNullOrEmpty(Get(q, "plugin"))) throw new UserError("Shadowsocks с плагином (obfs, v2ray-plugin) пока не поддерживается.");
        var outbound = new JsonObject
        {
            ["type"] = "shadowsocks", ["server"] = uri.Host.Trim('[', ']'), ["server_port"] = uri.Port > 0 ? uri.Port : 8388,
            ["method"] = credentials[..colon], ["password"] = credentials[(colon + 1)..]
        };
        return SingBoxProfiles.Parse(outbound, name);
    }

    // vmess://BASE64(JSON v2rayN): add, port, id, aid, scy, net, type, host, path, tls, sni, alpn, fp.
    private static Profile Vmess(string input)
    {
        var json = Base64Text(input["vmess://".Length..].Split('#')[0]) ?? throw new UserError("Не удалось прочитать ссылку VMess.");
        JsonObject v;
        try { v = JsonNode.Parse(json) as JsonObject ?? throw new UserError("Не удалось прочитать ссылку VMess."); }
        catch (JsonException) { throw new UserError("Не удалось прочитать ссылку VMess."); }
        string S(string key) => v[key] is JsonValue value ? value.ToString().Trim() : "";
        if (S("allowInsecure") is "1" or "true" || S("skip-cert-verify") is "1" or "true") throw new UserError("Ссылка отключает проверку TLS-сертификата.");
        var outbound = new JsonObject
        {
            ["type"] = "vmess", ["server"] = S("add"), ["server_port"] = int.TryParse(S("port"), out var port) ? port : 0,
            ["uuid"] = S("id"), ["security"] = S("scy") is { Length: > 0 } scy ? scy : "auto", ["alter_id"] = int.TryParse(S("aid"), out var aid) ? aid : 0
        };
        var host = S("host");
        switch (S("net") is { Length: > 0 } net ? net.ToLowerInvariant() : "tcp")
        {
            case "tcp" or "raw":
                if (S("type") is { Length: > 0 } header && header != "none") throw new UserError("VMess с маскировкой TCP под HTTP пока не поддерживается.");
                break;
            case "ws":
                var ws = new JsonObject { ["type"] = "ws", ["path"] = S("path") is { Length: > 0 } wsPath ? wsPath : "/" };
                if (host.Length > 0) ws["headers"] = new JsonObject { ["Host"] = host };
                outbound["transport"] = ws;
                break;
            case "grpc": outbound["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = S("path") }; break;
            case "h2" or "http":
                var http = new JsonObject { ["type"] = "http", ["path"] = S("path") is { Length: > 0 } h2Path ? h2Path : "/" };
                if (host.Length > 0) http["host"] = new JsonArray(host.Split(',').Select(h => (JsonNode?)JsonValue.Create(h.Trim())).ToArray());
                outbound["transport"] = http;
                break;
            case "httpupgrade": outbound["transport"] = new JsonObject { ["type"] = "httpupgrade", ["host"] = host, ["path"] = S("path") is { Length: > 0 } upPath ? upPath : "/" }; break;
            default: throw new UserError("Этот транспорт VMess пока не поддерживается.");
        }
        switch (S("tls").ToLowerInvariant())
        {
            case "tls":
                var tls = new JsonObject { ["enabled"] = true, ["server_name"] = S("sni") is { Length: > 0 } sni ? sni : host.Length > 0 ? host : S("add") };
                if (S("alpn").Length > 0) tls["alpn"] = new JsonArray(S("alpn").Split(',').Select(a => (JsonNode?)JsonValue.Create(a.Trim())).ToArray());
                if (S("fp").Length > 0) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = S("fp") };
                outbound["tls"] = tls;
                break;
            case "" or "none": break;
            default: throw new UserError("VMess с REALITY или другой защитой пока не поддерживается.");
        }
        return SingBoxProfiles.Parse(outbound, S("ps"));
    }

    // tuic://uuid:password@host:port?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=…
    private static Profile Tuic(Uri uri, Dictionary<string, string> q, string name)
    {
        RejectUnknown(q, new[] { "congestion_control", "udp_relay_mode", "alpn", "sni", "allow_insecure", "insecure", "disable_sni" });
        Insecure(q, "allow_insecure", "insecure");
        var parts = Uri.UnescapeDataString(uri.UserInfo).Split(':', 2);
        if (parts.Length != 2) throw new UserError("В ссылке TUIC нужны UUID и пароль.");
        var outbound = new JsonObject
        {
            ["type"] = "tuic", ["server"] = uri.Host.Trim('[', ']'), ["server_port"] = uri.Port > 0 ? uri.Port : 443, ["uuid"] = parts[0], ["password"] = parts[1],
            ["tls"] = Tls(q, uri.Host, "h3")
        };
        if (q.ContainsKey("congestion_control")) outbound["congestion_control"] = Get(q, "congestion_control");
        if (q.ContainsKey("udp_relay_mode")) outbound["udp_relay_mode"] = Get(q, "udp_relay_mode");
        return SingBoxProfiles.Parse(outbound, name);
    }

    // anytls://password@host:port/?sni=…&fp=…
    private static Profile AnyTls(Uri uri, Dictionary<string, string> q, string name)
    {
        RejectUnknown(q, new[] { "sni", "peer", "fp", "alpn", "insecure", "allowInsecure" });
        Insecure(q, "insecure", "allowInsecure");
        var password = Uri.UnescapeDataString(uri.UserInfo);
        if (q.ContainsKey("peer") && !q.ContainsKey("sni")) q["sni"] = q["peer"];
        var tls = Tls(q, uri.Host, null);
        if (q.ContainsKey("fp")) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = Get(q, "fp") };
        var outbound = new JsonObject { ["type"] = "anytls", ["server"] = uri.Host.Trim('[', ']'), ["server_port"] = uri.Port > 0 ? uri.Port : 443, ["password"] = password, ["tls"] = tls };
        return SingBoxProfiles.Parse(outbound, name);
    }

    // wireguard://закрытый_ключ@host:port?publickey=…&address=10.0.0.2/32&mtu=1420&reserved=1,2,3
    // (имена параметров у v2rayN и Hiddify различаются — принимаются оба варианта).
    private static Profile WireGuardLink(Uri uri, Dictionary<string, string> q, string name)
    {
        string First(params string[] keys) => keys.Select(k => Get(q, k)).FirstOrDefault(v => v.Length > 0) ?? "";
        RejectUnknown(q, new[] { "publickey", "publicKey", "pk", "peer_public_key", "address", "ip", "local_address", "mtu", "reserved",
            "presharedkey", "preSharedKey", "psk", "keepalive", "allowedips" });
        return WireGuardConfig.Build(Uri.UnescapeDataString(uri.UserInfo), First("address", "ip", "local_address"), First("mtu"),
            uri.Host.Trim('[', ']'), uri.Port, First("publickey", "publicKey", "pk", "peer_public_key"), First("presharedkey", "preSharedKey", "psk"),
            First("reserved"), First("keepalive"), name);
    }

    private static JsonObject Tls(Dictionary<string, string> q, string host, string? defaultAlpn)
    {
        var tls = new JsonObject { ["enabled"] = true, ["server_name"] = Get(q, "sni", host.Trim('[', ']')) };
        var alpn = Get(q, "alpn", defaultAlpn ?? "");
        if (alpn.Length > 0) tls["alpn"] = new JsonArray(alpn.Split(',').Select(a => (JsonNode?)JsonValue.Create(a.Trim())).ToArray());
        if (Get(q, "disable_sni") is "1" or "true") tls["disable_sni"] = true;
        return tls;
    }

    private static void Insecure(Dictionary<string, string> q, params string[] keys)
    {
        if (keys.Any(k => Get(q, k) is "1" or "true")) throw new UserError("Ссылка отключает проверку TLS-сертификата.");
    }

    private static (string Body, string Name) SplitFragment(string text)
    {
        var hash = text.IndexOf('#');
        return hash < 0 ? (text, "") : (text[..hash], Uri.UnescapeDataString(text[(hash + 1)..]));
    }

    // Base64 и base64url, с выравниванием и без. null — не base64 или не UTF-8.
    internal static string? Base64Text(string text)
    {
        var value = text.Trim().Replace('-', '+').Replace('_', '/');
        if (value.Length % 4 != 0) value = value.PadRight(value.Length + 4 - value.Length % 4, '=');
        try { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value)); }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return null; }
    }
}

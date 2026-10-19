using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oblachka;

// Проверка выхода Xray, который ядро туннеля (TunCore) исполняет встроенным Xray: XHTTP и всё,
// чего sing-box не умеет так же (ECH, ранние данные ws, mux, VLESS Encryption). Как и
// DirectOutboundPolicy, выход собирается заново из проверенных значений. VLESS и Trojan; сервер —
// IP из адресов аренды; без sockopt (detour, привязка к интерфейсу, метка), без отдельного адреса
// загрузки XHTTP (downloadSettings — другой сервер), без файлов сертификатов и allowInsecure.
// Неизвестное поле — отказ: окно тогда ведёт профиль прежним путём через своё ядро.
public static partial class XrayOutboundPolicy
{
    private static readonly string[] Fingerprints = DirectOutbound.Fingerprints.Append("randomizednoalpn").ToArray();
    private static readonly string[] Networks = { "raw", "tcp", "xhttp", "splithttp", "ws", "grpc", "httpupgrade" };

    // Тонкие настройки XHTTP: числа и диапазоны, флаги, короткие слова (места и имена полей).
    private static readonly string[] XhttpRanges = { "xPaddingBytes", "uplinkChunkSize", "scMaxEachPostBytes", "scMinPostsIntervalMs", "scMaxBufferedPosts", "scStreamUpServerSecs" };
    private static readonly string[] XhttpFlags = { "noGRPCHeader", "noSSEHeader", "xPaddingObfsMode" };
    private static readonly string[] XhttpWords = { "xPaddingKey", "xPaddingHeader", "xPaddingPlacement", "xPaddingMethod", "uplinkHTTPMethod",
        "uplinkDataPlacement", "uplinkDataKey", "sessionPlacement", "sessionKey", "seqPlacement", "seqKey" };
    private static readonly string[] XmuxRanges = { "maxConcurrency", "maxConnections", "cMaxReuseTimes", "hMaxRequestTimes", "hMaxReusableSecs" };

    public static JsonObject Validate(JsonNode? node, IReadOnlyList<IPAddress> peers)
    {
        if (node is not JsonObject outbound) throw Bad("выход");
        Only(outbound, "protocol", "settings", "streamSettings", "mux");
        var protocol = Text(outbound, "protocol");
        if (outbound["settings"] is not JsonObject settings) throw Bad("settings");
        var result = new JsonObject { ["protocol"] = protocol };
        string flow = "";
        switch (protocol)
        {
            case "vless":
                Only(settings, "vnext");
                var peer = One(settings, "vnext");
                Only(peer, "address", "port", "users");
                var user = One(peer, "users");
                Only(user, "id", "encryption", "flow");
                if (!Guid.TryParse(Text(user, "id"), out var id)) throw Bad("id");
                var encryption = Text(user, "encryption") ?? "none";
                if (encryption.Length is 0 or > 4096 || !Word().IsMatch(encryption)) throw Bad("encryption");
                flow = Text(user, "flow") ?? "";
                if (flow is not ("" or "xtls-rprx-vision" or "xtls-rprx-vision-udp443")) throw Bad("flow");
                var users = new JsonObject { ["id"] = id.ToString(), ["encryption"] = encryption };
                if (flow.Length > 0) users["flow"] = flow;
                result["settings"] = new JsonObject { ["vnext"] = new JsonArray(Server(peer, peers, users)) };
                break;
            case "trojan":
                Only(settings, "servers");
                var server = One(settings, "servers");
                Only(server, "address", "port", "password");
                var password = Text(server, "password");
                if (password is not { Length: > 0 and <= 512 }) throw Bad("password");
                var endpoint = Server(server, peers, null);
                endpoint["password"] = password;
                result["settings"] = new JsonObject { ["servers"] = new JsonArray(endpoint) };
                break;
            default: throw new UserError("Ядро туннеля принимает выход Xray только VLESS или Trojan.");
        }
        var stream = outbound["streamSettings"] as JsonObject ?? (outbound["streamSettings"] is null ? new JsonObject() : throw Bad("streamSettings"));
        result["streamSettings"] = Stream(stream, protocol == "trojan" ? "tls" : "none", flow.Length > 0);
        if (outbound["mux"] is JsonObject mux) result["mux"] = Mux(mux);
        else if (outbound["mux"] is not null) throw Bad("mux");
        return result;
    }

    private static JsonObject Server(JsonObject entry, IReadOnlyList<IPAddress> peers, JsonObject? user)
    {
        var address = Text(entry, "address");
        if (address is null || !IPAddress.TryParse(address, out var ip) || !peers.Contains(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip))
            throw new UserError("Сервер выхода должен быть одним из адресов этой сессии.");
        var port = Number(entry, "port");
        if (port is < 1 or > 65535) throw Bad("port");
        var result = new JsonObject { ["address"] = ip.ToString(), ["port"] = port };
        if (user is not null) result["users"] = new JsonArray(user);
        return result;
    }

    private static JsonObject Stream(JsonObject stream, string defaultSecurity, bool vision)
    {
        Only(stream, "network", "security", "tlsSettings", "realitySettings", "xhttpSettings", "splithttpSettings", "wsSettings",
            "grpcSettings", "httpupgradeSettings", "rawSettings", "tcpSettings");
        var network = Text(stream, "network") ?? "raw";
        var security = Text(stream, "security") ?? defaultSecurity;
        if (!Networks.Contains(network)) throw new UserError("Ядро туннеля не принимает транспорт " + network + ".");
        if (vision && (network is not ("raw" or "tcp") || security == "none")) throw new UserError("Vision работает только поверх TCP с TLS или REALITY.");
        var result = new JsonObject { ["network"] = network, ["security"] = security };
        switch (security)
        {
            case "none": break;
            case "tls": result["tlsSettings"] = Tls(stream["tlsSettings"] as JsonObject ?? new JsonObject()); break;
            case "reality": result["realitySettings"] = Reality(stream["realitySettings"] as JsonObject ?? throw Bad("realitySettings")); break;
            default: throw Bad("security");
        }
        foreach (var key in new[] { "tlsSettings", "realitySettings" })
            if (stream[key] is not null && result[key] is null) throw Bad(key);
        var own = network + "Settings";
        foreach (var key in new[] { "xhttpSettings", "splithttpSettings", "wsSettings", "grpcSettings", "httpupgradeSettings", "rawSettings", "tcpSettings" })
            if (stream[key] is not null && key != own) throw Bad(key);
        if (stream[own] is { } settings)
            result[own] = network switch
            {
                "raw" or "tcp" => Raw(settings as JsonObject ?? throw Bad(own)),
                "xhttp" or "splithttp" => Xhttp(settings as JsonObject ?? throw Bad(own)),
                "ws" => Ws(settings as JsonObject ?? throw Bad(own)),
                "grpc" => Grpc(settings as JsonObject ?? throw Bad(own)),
                _ => Upgrade(settings as JsonObject ?? throw Bad(own))
            };
        return result;
    }

    private static JsonObject Tls(JsonObject tls)
    {
        Only(tls, "serverName", "fingerprint", "alpn", "echConfigList", "allowInsecure");
        if (tls["allowInsecure"] is { } insecure && (insecure is not JsonValue v || !v.TryGetValue<bool>(out var flag) || flag))
            throw new UserError("Проверку сертификата у выхода ядра туннеля не отключить.");
        var result = new JsonObject { ["serverName"] = Host(Text(tls, "serverName"), "serverName") };
        if (Text(tls, "fingerprint") is { } fp) result["fingerprint"] = Fingerprints.Contains(fp) ? fp : throw Bad("fingerprint");
        if (tls["alpn"] is { } alpn) result["alpn"] = Alpn(alpn);
        // Только сам список ECH в base64: форма «имя+https://резолвер» отправила бы запрос DNS
        // куда-то кроме сервера.
        if (Text(tls, "echConfigList") is { } ech)
            result["echConfigList"] = ech.Length is > 0 and <= 8192 && Base64().IsMatch(ech) ? ech : throw Bad("echConfigList");
        return result;
    }

    private static JsonObject Reality(JsonObject reality)
    {
        Only(reality, "serverName", "fingerprint", "publicKey", "password", "shortId", "spiderX", "mldsa65Verify");
        var key = Text(reality, "publicKey") ?? Text(reality, "password");
        if (key is not { Length: >= 40 and <= 64 } || !Base64Url().IsMatch(key)) throw Bad("publicKey");
        var shortId = Text(reality, "shortId") ?? "";
        if (shortId.Length > 16 || !shortId.All(char.IsAsciiHexDigit)) throw Bad("shortId");
        var fp = Text(reality, "fingerprint") ?? "chrome";
        if (!Fingerprints.Contains(fp)) throw Bad("fingerprint");
        var result = new JsonObject
        {
            ["serverName"] = Host(Text(reality, "serverName"), "serverName"), ["fingerprint"] = fp, ["publicKey"] = key, ["shortId"] = shortId
        };
        if (Text(reality, "spiderX") is { } spider) result["spiderX"] = PathOf(spider, "spiderX");
        if (Text(reality, "mldsa65Verify") is { Length: > 0 } pqv)
            result["mldsa65Verify"] = pqv.Length <= 4096 && Base64Url().IsMatch(pqv) ? pqv : throw Bad("mldsa65Verify");
        return result;
    }

    private static JsonObject Mux(JsonObject mux)
    {
        Only(mux, "enabled", "concurrency", "xudpConcurrency", "xudpProxyUDP443");
        var result = new JsonObject { ["enabled"] = Flag(mux, "enabled") };
        foreach (var key in new[] { "concurrency", "xudpConcurrency" })
            if (mux[key] is not null) result[key] = Number(mux, key) is >= -1 and <= 1024 and var n ? n : throw Bad(key);
        if (Text(mux, "xudpProxyUDP443") is { } udp) result["xudpProxyUDP443"] = udp is "reject" or "allow" or "skip" ? udp : throw Bad("xudpProxyUDP443");
        return result;
    }

    internal static UserError Bad(string field) => new("Недопустимое поле выхода Xray: " + field + ".");

    private static void Only(JsonObject node, params string[] keys)
    {
        foreach (var property in node)
            if (!keys.Contains(property.Key, StringComparer.Ordinal)) throw Bad(property.Key);
    }

    private static JsonObject One(JsonObject node, string key) =>
        node[key] is JsonArray { Count: 1 } array && array[0] is JsonObject item ? item : throw Bad(key);

    private static bool Flag(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : throw Bad(key);

    private static int Number(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<int>(out var number) ? number : 0;

    private static string? Text(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : node[key] is null ? null : throw Bad(key);

    [GeneratedRegex("^[A-Za-z0-9._-]+$")] private static partial Regex Word();
    [GeneratedRegex("^[A-Za-z0-9+/=]+$")] private static partial Regex Base64();
    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex Base64Url();
}

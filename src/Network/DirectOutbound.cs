using System.Text.Json.Nodes;

namespace Oblachka;

// Выход ядра туннеля прямо на сервер («одно ядро»): профиль Xray переводится в outbound sing-box,
// и трафик идёт из TUN на сервер без SOCKS и второго ядра. Замер 18.10.2026 на машине владельца:
// скорость +45–60%, процессор на гигабайт −22%, новое соединение 1,2 мс против 1,6, памяти −60 МБ.
//
// Перевод осторожный: только то, что sing-box умеет так же, как Xray. Всё прочее (XHTTP, ECH,
// ранние данные ws, mux, VLESS Encryption) ядро туннеля исполняет встроенным Xray — выход
// "xray" (TunCore, XrayOutboundPolicy). Чего не принимает и он — null, и профиль идёт прежним
// путём через ядро окна. Ошибиться здесь — значит молча изменить защиту соединения, поэтому лучше null.
public static class DirectOutbound
{
    // Отпечатки uTLS, одинаково понятные Xray и sing-box.
    public static readonly string[] Fingerprints = { "chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized" };

    private static readonly string[] StreamKeys = { "network", "security", "realitySettings", "tlsSettings", "wsSettings", "grpcSettings", "httpupgradeSettings" };

    // pinned — профиль из TunnelEndpointPlan: адрес сервера уже IP из аренды.
    public static JsonObject? From(Profile pinned)
    {
        // Поле неожиданного типа в профиле (строка вместо числа) — тоже «не переводится».
        // Окно проверяет результат той же политикой, что и служба: выход, который служба
        // отклонила бы, не должен сорвать подключение — профиль просто идёт прежним путём.
        try
        {
            pinned = Utls.For(pinned);
            var outbound = Translate(pinned) ?? Bridge(pinned);
            var server = outbound?["type"]?.GetValue<string>() == "xray" ? ProfileServer.Of(pinned.Outbound, pinned.Core).Host : outbound?["server"]?.GetValue<string>();
            return outbound is not null && System.Net.IPAddress.TryParse(server, out var ip)
                ? DirectOutboundPolicy.Validate(outbound, new[] { ip }) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UserError) { return null; }
    }

    // Профиль Xray как есть — встроенному Xray ядра туннеля. Убирается только то, что на путь
    // трафика не влияет: метка, уровень и почта пользователя, выключенный mux, настройки сокета
    // без перенаправления (их набиратель ядра туннеля всё равно не применяет).
    // Аренда идёт одним сообщением канала (HelperProtocol.MaxBytes). Выходы с постквантовыми
    // ключами и ECH весят килобайты: что не помещается, идёт прежним путём через ядро окна.
    public const int LeaseBudget = 32 * 1024;

    public static JsonObject?[] Fit(JsonObject?[] outbounds, int budget = LeaseBudget)
    {
        var used = 0;
        return outbounds.Select(o =>
        {
            if (o is null) return null;
            var size = System.Text.Encoding.UTF8.GetByteCount(o.ToJsonString());
            if (used + size > budget) return null;
            used += size;
            return o;
        }).ToArray();
    }

    private static JsonObject? Bridge(Profile pinned)
    {
        if (pinned.Core != CoreKind.Xray) return null;
        var copy = (JsonObject)pinned.Outbound.DeepClone();
        copy.Remove("tag");
        foreach (var user in copy["settings"]?["vnext"]?[0]?["users"] as JsonArray ?? [])
            if (user is JsonObject u) { u.Remove("level"); u.Remove("email"); if (Text(u["flow"]) == "") u.Remove("flow"); }
        foreach (var server in copy["settings"]?["servers"] as JsonArray ?? [])
            if (server is JsonObject s) { s.Remove("level"); s.Remove("email"); }
        if (copy["mux"] is JsonObject mux && mux["enabled"] is JsonValue on && on.TryGetValue<bool>(out var enabled) && !enabled) copy.Remove("mux");
        if (copy["streamSettings"] is JsonObject stream && stream["sockopt"] is JsonObject sockopt &&
            sockopt.All(p => p.Key is "tcpFastOpen" or "tcpNoDelay" or "tcpKeepAliveIdle" or "tcpKeepAliveInterval" or "domainStrategy"))
            stream.Remove("sockopt");
        return new JsonObject { ["type"] = "xray", ["xray"] = copy };
    }

    private static JsonObject? Translate(Profile pinned)
    {
        // Профиль sing-box (ss://, JSON sing-box) уже в нужном виде: его поля проверит политика.
        if (pinned.Core == CoreKind.SingBox)
        {
            if (Text(pinned.Outbound["type"]) is not ("vless" or "trojan" or "shadowsocks" or "amneziawg")) return null;
            var copy = (JsonObject)pinned.Outbound.DeepClone();
            copy.Remove("tag");
            return copy;
        }
        var o = pinned.Outbound;
        if (o["mux"] is JsonObject mux && mux["enabled"]?.GetValue<bool>() == true) return null;
        var protocol = Text(o["protocol"]);
        var settings = o["settings"] as JsonObject;
        if (settings is null) return null;
        JsonObject? result = protocol switch
        {
            "vless" => Vless(settings),
            "trojan" => Trojan(settings),
            "shadowsocks" => Shadowsocks(settings),
            _ => null
        };
        if (result is null) return null;
        if (protocol == "shadowsocks")
            return o["streamSettings"] is JsonObject s && (s.Count > 1 || Text(s["network"]) is not (null or "tcp" or "raw")) ? null : result;
        var stream = o["streamSettings"] as JsonObject ?? new JsonObject();
        return Stream(stream, result, protocol == "trojan" ? "tls" : "none") ? result : null;
    }

    private static JsonObject? Vless(JsonObject settings)
    {
        if (One(settings["vnext"]) is not { } peer || One(peer["users"]) is not { } user) return null;
        if (Text(user["encryption"]) is not (null or "" or "none")) return null;
        var flow = Text(user["flow"]) ?? "";
        if (flow is not ("" or "xtls-rprx-vision")) return null;
        if (Endpoint(peer, "vless") is not { } result || Text(user["id"]) is not { } id || !Guid.TryParse(id, out _)) return null;
        result["uuid"] = id;
        if (flow.Length > 0) result["flow"] = flow;
        // UDP через VLESS — XUDP, как у клиента Xray по умолчанию.
        result["packet_encoding"] = "xudp";
        return result;
    }

    private static JsonObject? Trojan(JsonObject settings)
    {
        if (One(settings["servers"]) is not { } server || Endpoint(server, "trojan") is not { } result) return null;
        if (Text(server["password"]) is not { Length: > 0 } password) return null;
        result["password"] = password;
        return result;
    }

    private static JsonObject? Shadowsocks(JsonObject settings)
    {
        if (One(settings["servers"]) is not { } server || Endpoint(server, "shadowsocks") is not { } result) return null;
        if (server["uot"] is not null || server["UoT"] is not null) return null;
        var method = Text(server["method"])?.ToLowerInvariant();
        if (method is null || !XrayProtocols.ShadowsocksMethods.Contains(method) || Text(server["password"]) is not { Length: > 0 } password) return null;
        result["method"] = method; result["password"] = password;
        return result;
    }

    private static bool Stream(JsonObject stream, JsonObject result, string defaultSecurity)
    {
        if (stream.Any(p => !StreamKeys.Contains(p.Key))) return false;
        var network = Text(stream["network"]) ?? "tcp";
        var security = Text(stream["security"]) ?? defaultSecurity;
        switch (security)
        {
            case "none": break;
            case "reality":
                if (stream["realitySettings"] is not JsonObject r || r["mldsa65Verify"] is { } pqv && Text(pqv) is { Length: > 0 }) return false;
                if (Text(r["serverName"]) is not { Length: > 0 } sni || Text(r["publicKey"]) is not { Length: > 0 } key) return false;
                var fp = Text(r["fingerprint"]) ?? "chrome";
                if (!Fingerprints.Contains(fp)) return false;
                result["tls"] = new JsonObject
                {
                    ["enabled"] = true, ["server_name"] = sni,
                    ["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp },
                    ["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = key, ["short_id"] = Text(r["shortId"]) ?? "" }
                };
                break;
            case "tls":
                var t = stream["tlsSettings"] as JsonObject ?? new JsonObject();
                if (t["allowInsecure"]?.GetValue<bool>() == true || Text(t["echConfigList"]) is { Length: > 0 }) return false;
                if (t.Any(p => p.Key is not ("serverName" or "allowInsecure" or "fingerprint" or "alpn" or "echConfigList"))) return false;
                if (Text(t["serverName"]) is not { Length: > 0 } name) return false;
                var tls = new JsonObject { ["enabled"] = true, ["server_name"] = name };
                if (Text(t["fingerprint"]) is { Length: > 0 } tfp)
                {
                    if (!Fingerprints.Contains(tfp)) return false;
                    tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = tfp };
                }
                if (t["alpn"] is JsonArray alpn) tls["alpn"] = alpn.DeepClone();
                result["tls"] = tls;
                break;
            default: return false;
        }
        switch (network)
        {
            case "tcp" or "raw": break;
            case "ws":
                var ws = stream["wsSettings"] as JsonObject ?? new JsonObject();
                var path = Text(ws["path"]) ?? "/";
                // ?ed= — ранние данные Xray: в sing-box это другие поля, перевод не проверен.
                if (path.Contains("?ed=", StringComparison.OrdinalIgnoreCase) || ws.Any(p => p.Key is not ("path" or "headers"))) return false;
                var transport = new JsonObject { ["type"] = "ws", ["path"] = path };
                if (ws["headers"] is JsonObject h && Text(h["Host"]) is { Length: > 0 } host) transport["headers"] = new JsonObject { ["Host"] = host };
                result["transport"] = transport;
                break;
            case "grpc":
                var g = stream["grpcSettings"] as JsonObject ?? new JsonObject();
                if (Text(g["authority"]) is { Length: > 0 } || g.Any(p => p.Key is not ("serviceName" or "authority"))) return false;
                result["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = Text(g["serviceName"]) ?? "" };
                break;
            case "httpupgrade":
                var u = stream["httpupgradeSettings"] as JsonObject ?? new JsonObject();
                if (u.Any(p => p.Key is not ("path" or "host"))) return false;
                var upgrade = new JsonObject { ["type"] = "httpupgrade", ["path"] = Text(u["path"]) ?? "/" };
                if (Text(u["host"]) is { Length: > 0 } uhost) upgrade["host"] = uhost;
                result["transport"] = upgrade;
                break;
            default: return false;
        }
        // Vision работает только поверх TCP с TLS или REALITY.
        if (Text(result["flow"]) is { Length: > 0 } && (network is not ("tcp" or "raw") || security == "none")) return false;
        return true;
    }

    private static JsonObject? Endpoint(JsonObject server, string type)
    {
        var address = Text(server["address"]);
        var port = server["port"] is JsonValue p && p.TryGetValue<int>(out var number) ? number : 0;
        if (address is null || !System.Net.IPAddress.TryParse(address, out _) || port is < 1 or > 65535) return null;
        return new JsonObject { ["type"] = type, ["server"] = address, ["server_port"] = port };
    }

    private static JsonObject? One(JsonNode? node) => node is JsonArray { Count: 1 } array ? array[0] as JsonObject : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

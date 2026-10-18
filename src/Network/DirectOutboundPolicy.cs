using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

// Проверка выхода «прямо на сервер» в службе. Служба работает от SYSTEM, а outbound приходит от окна,
// поэтому ничего не берётся как есть: выход собирается заново из проверенных значений. Разрешены
// только VLESS, Trojan и Shadowsocks (и они же в формате Xray — XrayOutboundPolicy); сервер — точный IP из адресов аренды (их же разрешает защита
// WFP); проверка сертификата не отключается; нет путей к файлам, detour, привязки к интерфейсу и
// прочих полей, которыми ядро от SYSTEM можно было бы направить на что-то кроме сервера.
// Ключи живут только в памяти: в журнал сессии и в отчёты они не попадают.
public static class DirectOutboundPolicy
{
    public const int MaxOutbounds = 16;

    public static JsonObject Validate(JsonObject outbound, IReadOnlyList<IPAddress> peers)
    {
        var type = Text(outbound, "type");
        if (type == "xray")
        {
            Only(outbound, "type", "xray");
            return new JsonObject { ["type"] = "xray", ["xray"] = XrayOutboundPolicy.Validate(outbound["xray"], peers) };
        }
        var allowed = type switch
        {
            "vless" => new[] { "type", "server", "server_port", "uuid", "flow", "packet_encoding", "tls", "transport" },
            "trojan" => new[] { "type", "server", "server_port", "password", "tls", "transport" },
            "shadowsocks" => new[] { "type", "server", "server_port", "method", "password" },
            _ => throw new UserError("Служба принимает выход только VLESS, Trojan или Shadowsocks.")
        };
        Only(outbound, allowed);
        var server = Text(outbound, "server");
        if (server is null || !IPAddress.TryParse(server, out var ip) || !peers.Contains(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip))
            throw new UserError("Сервер выхода должен быть одним из адресов этой сессии.");
        var port = Number(outbound, "server_port");
        if (port is < 1 or > 65535) throw new UserError("Неверный порт сервера выхода.");
        var result = new JsonObject { ["type"] = type, ["server"] = ip.ToString(), ["server_port"] = port };
        switch (type)
        {
            case "vless":
                if (!Guid.TryParse(Text(outbound, "uuid"), out var id)) throw new UserError("Неверный UUID выхода VLESS.");
                result["uuid"] = id.ToString();
                if (Text(outbound, "flow") is { } flow)
                {
                    if (flow != "xtls-rprx-vision") throw new UserError("Неподдерживаемый flow выхода VLESS.");
                    result["flow"] = flow;
                }
                if (Text(outbound, "packet_encoding") is { } encoding)
                {
                    if (encoding != "xudp") throw new UserError("Неподдерживаемое кодирование UDP выхода VLESS.");
                    result["packet_encoding"] = encoding;
                }
                break;
            case "trojan":
                result["password"] = Secret(outbound, "password");
                break;
            default:
                var method = Text(outbound, "method");
                if (method is null || !XrayProtocols.ShadowsocksMethods.Contains(method)) throw new UserError("Неподдерживаемый метод Shadowsocks.");
                result["method"] = method; result["password"] = Secret(outbound, "password");
                break;
        }
        if (outbound["tls"] is JsonObject tls) result["tls"] = Tls(tls);
        else if (outbound["tls"] is not null || type == "trojan") throw new UserError("Выходу Trojan нужен TLS.");
        if (outbound["transport"] is JsonObject transport) result["transport"] = Transport(transport);
        else if (outbound["transport"] is not null) throw new UserError("Неверный транспорт выхода.");
        if (result["flow"] is not null && (result["tls"] is null || result["transport"] is not null))
            throw new UserError("Vision работает только поверх TCP с TLS или REALITY.");
        return result;
    }

    private static JsonObject Tls(JsonObject tls)
    {
        Only(tls, "enabled", "server_name", "utls", "reality", "alpn");
        if (!True(tls, "enabled")) throw new UserError("TLS выхода должен быть включён.");
        var name = Text(tls, "server_name");
        if (name is not { Length: > 0 and <= 253 } || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
            throw new UserError("Неверное имя сервера TLS выхода.");
        var result = new JsonObject { ["enabled"] = true, ["server_name"] = name };
        if (tls["utls"] is JsonObject utls)
        {
            Only(utls, "enabled", "fingerprint");
            var fp = Text(utls, "fingerprint");
            if (!True(utls, "enabled") || fp is null || !DirectOutbound.Fingerprints.Contains(fp)) throw new UserError("Неверный отпечаток uTLS выхода.");
            result["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
        }
        if (tls["reality"] is JsonObject reality)
        {
            Only(reality, "enabled", "public_key", "short_id");
            var key = Text(reality, "public_key"); var shortId = Text(reality, "short_id") ?? "";
            if (!True(reality, "enabled") || key is not { Length: >= 40 and <= 64 } || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                shortId.Length > 16 || !shortId.All(char.IsAsciiHexDigit) || result["utls"] is null)
                throw new UserError("Неверные параметры REALITY выхода.");
            result["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = key, ["short_id"] = shortId };
        }
        if (tls["alpn"] is JsonArray alpn)
        {
            var values = alpn.Select(a => a is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToArray();
            if (values.Length is 0 or > 4 || values.Any(v => v is not ("h2" or "http/1.1" or "h3"))) throw new UserError("Неверный ALPN выхода.");
            result["alpn"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }
        return result;
    }

    private static JsonObject Transport(JsonObject transport)
    {
        var type = Text(transport, "type");
        static string PathOf(JsonObject t)
        {
            var path = Text(t, "path") ?? "/";
            if (!path.StartsWith('/') || path.Length > 256 || path.Any(c => c < 0x21 || c > 0x7e)) throw new UserError("Неверный путь транспорта выхода.");
            return path;
        }
        static string HostOf(string? host) => host is { Length: > 0 and <= 253 } && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')
            ? host : throw new UserError("Неверный Host транспорта выхода.");
        switch (type)
        {
            case "ws":
                Only(transport, "type", "path", "headers");
                var ws = new JsonObject { ["type"] = "ws", ["path"] = PathOf(transport) };
                if (transport["headers"] is JsonObject headers)
                {
                    Only(headers, "Host");
                    ws["headers"] = new JsonObject { ["Host"] = HostOf(Text(headers, "Host")) };
                }
                return ws;
            case "grpc":
                Only(transport, "type", "service_name");
                var service = Text(transport, "service_name") ?? "";
                if (service.Length > 128 || service.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/')))
                    throw new UserError("Неверное имя сервиса gRPC выхода.");
                return new JsonObject { ["type"] = "grpc", ["service_name"] = service };
            case "httpupgrade":
                Only(transport, "type", "path", "host");
                var upgrade = new JsonObject { ["type"] = "httpupgrade", ["path"] = PathOf(transport) };
                if (Text(transport, "host") is { } host) upgrade["host"] = HostOf(host);
                return upgrade;
            default: throw new UserError("Служба принимает транспорт выхода только ws, grpc или httpupgrade.");
        }
    }

    private static void Only(JsonObject node, params string[] keys)
    {
        foreach (var property in node)
            if (!keys.Contains(property.Key, StringComparer.Ordinal)) throw new UserError("Недопустимое поле выхода: " + property.Key + ".");
    }

    private static string Secret(JsonObject node, string key) =>
        Text(node, key) is { Length: > 0 and <= 512 } value ? value : throw new UserError("Нет ключа выхода.");

    private static bool True(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;

    private static int Number(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<int>(out var number) ? number : 0;

    private static string? Text(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oblachka;

// Транспорты выхода Xray для ядра туннеля: значения пересобираются, неизвестное поле — отказ.
public static partial class XrayOutboundPolicy
{
    private static JsonObject Raw(JsonObject raw)
    {
        Only(raw, "header");
        if (raw["header"] is { } header && (header is not JsonObject h || h.Count != 1 || Text(h, "type") != "none")) throw Bad("header");
        return new JsonObject();
    }

    private static JsonObject Xhttp(JsonObject xhttp)
    {
        Only(xhttp, [.. XhttpRanges, .. XhttpFlags, .. XhttpWords, "host", "path", "mode", "extra", "headers", "xmux"]);
        var result = new JsonObject { ["path"] = PathOf(Text(xhttp, "path") ?? "/", "path") };
        if (Text(xhttp, "host") is { Length: > 0 } host) result["host"] = Host(host, "host");
        var mode = Text(xhttp, "mode") ?? "auto";
        result["mode"] = mode is "auto" or "packet-up" or "stream-up" or "stream-one" ? mode : throw Bad("mode");
        // Тонкие настройки бывают и прямо в xhttpSettings, и в extra — проверяются одинаково.
        Tuning(xhttp, result);
        if (xhttp["extra"] is JsonObject extra)
        {
            Only(extra, [.. XhttpRanges, .. XhttpFlags, .. XhttpWords, "headers", "xmux"]);
            var tuned = new JsonObject();
            Tuning(extra, tuned);
            if (tuned.Count > 0) result["extra"] = tuned;
        }
        else if (xhttp["extra"] is not null) throw Bad("extra");
        return result;
    }

    private static void Tuning(JsonObject from, JsonObject to)
    {
        foreach (var key in XhttpRanges) if (from[key] is { } value) to[key] = Range(value, key);
        foreach (var key in XhttpFlags) if (from[key] is not null) to[key] = Flag(from, key);
        foreach (var key in XhttpWords)
            if (Text(from, key) is { } word) to[key] = word.Length <= 64 && (word.Length == 0 || Word().IsMatch(word)) ? word : throw Bad(key);
        if (from["headers"] is { } headers) to["headers"] = Headers(headers);
        if (from["xmux"] is JsonObject xmux)
        {
            Only(xmux, [.. XmuxRanges, "hKeepAlivePeriod"]);
            var result = new JsonObject();
            foreach (var key in XmuxRanges) if (xmux[key] is { } value) result[key] = Range(value, key);
            if (xmux["hKeepAlivePeriod"] is not null)
                result["hKeepAlivePeriod"] = Number(xmux, "hKeepAlivePeriod") is >= -1 and <= 3600 and var period ? period : throw Bad("hKeepAlivePeriod");
            to["xmux"] = result;
        }
        else if (from["xmux"] is not null) throw Bad("xmux");
    }

    private static JsonObject Ws(JsonObject ws)
    {
        Only(ws, "path", "host", "headers", "heartbeatPeriod");
        // Ранние данные (?ed=) Xray понимает сам: путь берётся как есть, если он печатный.
        var result = new JsonObject { ["path"] = PathOf(Text(ws, "path") ?? "/", "path") };
        if (Text(ws, "host") is { Length: > 0 } host) result["host"] = Host(host, "host");
        if (ws["headers"] is { } headers) result["headers"] = Headers(headers);
        if (ws["heartbeatPeriod"] is not null)
            result["heartbeatPeriod"] = Number(ws, "heartbeatPeriod") is >= 0 and <= 3600 and var period ? period : throw Bad("heartbeatPeriod");
        return result;
    }

    private static JsonObject Grpc(JsonObject grpc)
    {
        Only(grpc, "serviceName", "authority", "multiMode");
        var service = Text(grpc, "serviceName") ?? "";
        if (service.Length > 128 || service.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/'))) throw Bad("serviceName");
        var result = new JsonObject { ["serviceName"] = service };
        if (Text(grpc, "authority") is { Length: > 0 } authority) result["authority"] = Host(authority, "authority");
        if (grpc["multiMode"] is not null) result["multiMode"] = Flag(grpc, "multiMode");
        return result;
    }

    private static JsonObject Upgrade(JsonObject upgrade)
    {
        Only(upgrade, "path", "host", "headers");
        var result = new JsonObject { ["path"] = PathOf(Text(upgrade, "path") ?? "/", "path") };
        if (Text(upgrade, "host") is { Length: > 0 } host) result["host"] = Host(host, "host");
        if (upgrade["headers"] is { } headers) result["headers"] = Headers(headers);
        return result;
    }

    private static JsonArray Alpn(JsonNode alpn)
    {
        var values = alpn is JsonArray array ? array.Select(a => a is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToArray() : [];
        if (values.Length is 0 or > 4 || values.Any(v => v is not ("h2" or "http/1.1" or "h3"))) throw Bad("alpn");
        return new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    }

    // Заголовки HTTP транспорта: имя — токен, значение — печатный ASCII.
    private static JsonObject Headers(JsonNode node)
    {
        if (node is not JsonObject headers || headers.Count > 16) throw Bad("headers");
        var result = new JsonObject();
        foreach (var (name, value) in headers)
        {
            if (!Word().IsMatch(name) || name.Length > 64) throw Bad("headers");
            var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw Bad("headers");
            if (text.Length > 1024 || text.Any(c => c < 0x20 || c > 0x7e)) throw Bad("headers");
            result[name] = text;
        }
        return result;
    }

    // Число или «от-до» строкой: так Xray задаёт размеры и интервалы XHTTP.
    private static JsonNode Range(JsonNode value, string key)
    {
        if (value is JsonValue v && v.TryGetValue<long>(out var number)) return number is >= 0 and <= int.MaxValue ? number : throw Bad(key);
        if (value is JsonValue t && t.TryGetValue<string>(out var text)) return RangeText().IsMatch(text) ? text : throw Bad(key);
        throw Bad(key);
    }

    private static string Host(string? host, string key) =>
        host is { Length: > 0 and <= 253 } && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']') ? host : throw Bad(key);

    private static string PathOf(string path, string key) =>
        path.StartsWith('/') && path.Length <= 512 && path.All(c => c is > ' ' and <= '~') ? path : throw Bad(key);

    [GeneratedRegex(@"^\d{1,9}(-\d{1,9})?$")] private static partial Regex RangeText();
}

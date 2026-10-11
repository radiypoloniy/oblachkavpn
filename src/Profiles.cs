using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot;

public sealed record Profile(string Name, string Transport, JsonObject Outbound, CoreKind Core = CoreKind.Xray)
{
    public ServerCountry Country => CountryCatalog.Resolve(Name);
    public string DisplayName => ServerLabels.DisplayName(Name);
    public override string ToString() => Name + " · " + Transport;
}

public class UserError(string message, Exception? inner = null) : Exception(message, inner);

public static class Profiles
{
    public const int MaxBytes = 1024 * 1024;
    public static string Fingerprint(Profile profile)
    {
        JsonNode? Canonical(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Canonical(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
            _ => node?.DeepClone()
        };
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile.Core + ":" + Canonical(profile.Outbound)!.ToJsonString())));
    }
    public static Profile Parse(string input)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(input) > MaxBytes)
            throw new UserError("Профиль слишком большой. Максимум — 1 МБ.");
        input = input.Trim();
        if (input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseLink(input);
        try
        {
            var root = JsonNode.Parse(input, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new UserError("Нужна VLESS-ссылка или JSON-объект Xray.");
            JsonObject outbound;
            if (root["outbounds"] is JsonArray outbounds)
            {
                var candidates = outbounds.OfType<JsonObject>().Where(n => n["protocol"]?.GetValue<string>() == "vless" || SingBoxProfiles.IsSupported(n)).ToList();
                if (candidates.Count != 1) throw new UserError("В конфигурации должен быть ровно один VLESS outbound. Экспортируй нужное подключение отдельно.");
                outbound = (JsonObject)candidates[0].DeepClone();
            }
            else outbound = (JsonObject)root.DeepClone();
            return outbound["type"] is not null ? SingBoxProfiles.Parse(outbound, "Импортированный профиль") : FromOutbound(outbound, "Импортированный профиль");
        }
        catch (UserError) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { throw new UserError("Не удалось прочитать JSON. Нужен клиентский outbound Xray или sing-box."); }
    }

    public static Profile FromOutbound(JsonObject outbound, string name)
    {
        RejectExternalResources(outbound);
        if (outbound["protocol"]?.GetValue<string>() != "vless")
            throw new UserError("В этой сборке поддерживается VLESS через Xray. Другие протоколы добавим следующим этапом.");
        if (outbound["proxySettings"] is not null || outbound["sendThrough"] is not null)
            throw new UserError("Профиль с цепочкой или привязкой интерфейса пока не поддерживается.");
        var settings = outbound["settings"] as JsonObject ?? throw new UserError("В профиле нет settings.");
        // Xray 26.9.9 uses vnext. Newer flat address/id syntax is intentionally not guessed.
        var vnext = settings["vnext"] as JsonArray;
        if (vnext?.Count != 1 || vnext[0] is not JsonObject peer || peer["users"] is not JsonArray users || users.Count != 1)
            throw new UserError("Для Xray 26.9.9 нужны settings.vnext с одним сервером и одним пользователем.");
        var address = peer["address"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace)) throw new UserError("Не указан корректный адрес сервера.");
        var port = peer["port"]?.GetValue<int>() ?? 0;
        if (port is < 1 or > 65535) throw new UserError("Порт сервера должен быть от 1 до 65535.");
        var user = users[0] as JsonObject ?? throw new UserError("Не указан пользователь VLESS.");
        if (string.IsNullOrWhiteSpace(user["id"]?.GetValue<string>())) throw new UserError("Не указан идентификатор VLESS.");
        var stream = outbound["streamSettings"] as JsonObject ?? throw new UserError("В профиле нет streamSettings.");
        var security = stream["security"]?.GetValue<string>() ?? "none";
        // Keep this first build on encrypted public transports, including for optional VLESS Encryption.
        if (security is not ("tls" or "reality")) throw new UserError("Первая сборка принимает профили с TLS или REALITY.");
        if (HasInsecureTls(stream))
            throw new UserError("Профиль отключает проверку TLS-сертификата. Используй профиль с проверкой сертификата.");
        var network = stream["method"]?.GetValue<string>() ?? stream["network"]?.GetValue<string>() ?? "tcp";
        network = network.ToLowerInvariant() switch { "websocket" => "ws", "splithttp" => "xhttp", var n => n };
        if (network is not ("tcp" or "raw" or "xhttp" or "ws" or "grpc" or "httpupgrade"))
            throw new UserError("Этот транспорт пока не поддерживается.");
        var sockopt = stream["sockopt"] as JsonObject;
        if (sockopt?["dialerProxy"] is not null) throw new UserError("Профиль с дополнительным dialerProxy пока не поддерживается.");
        outbound["tag"] = "tunnel";
        return new Profile(SafeName(name), network.ToUpperInvariant() + " / " + security.ToUpperInvariant(), outbound);
    }

    private static Profile ParseLink(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "vless" || string.IsNullOrWhiteSpace(uri.Host))
            throw new UserError("Не удалось прочитать VLESS-ссылку.");
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            if (!q.TryAdd(key, pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : ""))
                throw new UserError("В ссылке повторяется параметр. Экспортируй профиль как JSON.");
        }
        string Get(string key, string fallback = "") => q.TryGetValue(key, out var value) ? value : fallback;
        var known = new HashSet<string>(new[] { "type", "security", "encryption", "flow", "sni", "fp", "pbk", "sid", "spx", "alpn", "allowInsecure", "host", "path", "mode", "extra", "serviceName", "authority", "headerType", "packetEncoding" }, StringComparer.OrdinalIgnoreCase);
        if (q.Keys.Any(k => !known.Contains(k)))
            throw new UserError("Ссылка содержит параметры, которые эта сборка не распознаёт. Импортируй клиентский Xray JSON, чтобы сохранить настройки.");
        if (Get("allowInsecure") is "1" or "true") throw new UserError("Ссылка отключает проверку TLS-сертификата.");
        if (Get("headerType", "none") != "none" || q.ContainsKey("packetEncoding"))
            throw new UserError("Дополнительные параметры этой ссылки требуют импорта через Xray JSON.");
        var id = Uri.UnescapeDataString(uri.UserInfo);
        if (!Guid.TryParse(id, out _)) throw new UserError("В ссылке нужен UUID пользователя VLESS.");
        var network = Get("type", "tcp").ToLowerInvariant();
        if (network == "raw") network = "tcp";
        var security = Get("security", "none").ToLowerInvariant();
        var stream = new JsonObject { ["network"] = network, ["security"] = security };
        if (security == "reality")
        {
            if (string.IsNullOrWhiteSpace(Get("pbk")) || string.IsNullOrWhiteSpace(Get("sni")))
                throw new UserError("В REALITY-ссылке нужны pbk и sni.");
            stream["realitySettings"] = new JsonObject { ["serverName"] = Get("sni"), ["fingerprint"] = Get("fp", "chrome"), ["publicKey"] = Get("pbk"), ["shortId"] = Get("sid"), ["spiderX"] = Get("spx", "/") };
        }
        else if (security == "tls")
        {
            var tls = new JsonObject { ["serverName"] = Get("sni", uri.Host), ["allowInsecure"] = false };
            if (q.ContainsKey("fp")) tls["fingerprint"] = Get("fp");
            if (q.ContainsKey("alpn")) tls["alpn"] = new JsonArray(Get("alpn").Split(',').Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            stream["tlsSettings"] = tls;
        }
        switch (network)
        {
            case "xhttp":
                var xhttp = new JsonObject { ["path"] = Get("path", "/"), ["host"] = Get("host"), ["mode"] = Get("mode", "auto") };
                if (q.ContainsKey("extra"))
                {
                    try { xhttp["extra"] = JsonNode.Parse(Get("extra")) as JsonObject ?? throw new UserError("extra должен быть JSON-объектом."); }
                    catch (JsonException) { throw new UserError("Не удалось прочитать XHTTP extra. Импортируй Xray JSON."); }
                }
                stream["xhttpSettings"] = xhttp;
                break;
            case "ws":
                var ws = new JsonObject { ["path"] = Get("path", "/") };
                if (q.ContainsKey("host")) ws["headers"] = new JsonObject { ["Host"] = Get("host") };
                stream["wsSettings"] = ws;
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = Get("serviceName"), ["authority"] = Get("authority") };
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new JsonObject { ["path"] = Get("path", "/"), ["host"] = Get("host") };
                break;
        }
        var outbound = new JsonObject
        {
            ["protocol"] = "vless",
            ["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = uri.Host, ["port"] = uri.Port > 0 ? uri.Port : 443,
                ["users"] = new JsonArray(new JsonObject { ["id"] = id, ["encryption"] = Get("encryption", "none"), ["flow"] = Get("flow") }) }) },
            ["streamSettings"] = stream
        };
        return FromOutbound(outbound, Uri.UnescapeDataString(uri.Fragment.TrimStart('#')));
    }

    private static bool HasInsecureTls(JsonNode? node)
    {
        if (node is JsonObject obj)
            return obj.Any(pair => pair.Key.Equals("allowInsecure", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue value && value.TryGetValue<bool>(out var insecure) && insecure || HasInsecureTls(pair.Value));
        return node is JsonArray array && array.Any(HasInsecureTls);
    }

    private static void RejectExternalResources(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj)
            {
                // Imported profiles cannot ask the child process to read keys/files or write TLS secrets.
                if (new[] { "certificateFile", "keyFile", "masterKeyLog", "dialerProxy" }.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                    throw new UserError("Профиль с внешними файлами, журналом TLS-ключей или цепочкой прокси пока не поддерживается.");
                RejectExternalResources(pair.Value);
            }
        else if (node is JsonArray array) foreach (var item in array) RejectExternalResources(item);
    }

    public static JsonObject RuntimeConfig(JsonObject outbound, int socksPort)
    {
        var copy = (JsonObject)outbound.DeepClone();
        copy["tag"] = "tunnel";
        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "none" },
            ["inbounds"] = new JsonArray(new JsonObject { ["tag"] = "local", ["listen"] = "127.0.0.1", ["port"] = socksPort, ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false } }),
            ["outbounds"] = new JsonArray(copy),
            ["routing"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("local"), ["outboundTag"] = "tunnel" }) }
        };
    }

    private static string SafeName(string name)
    {
        var safe = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "VLESS-профиль" : safe;
    }
}

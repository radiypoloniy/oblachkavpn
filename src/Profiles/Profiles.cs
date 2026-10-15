using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

// Откуда взялось подключение. Узлы сервиса — основной путь: для них работают подбор,
// измерения и восстановление. Сторонние остаются доступными, но только вручную:
// их конфигурации мы не контролируем и отвечать за них не можем.
public enum ProfileSource { Service, External }

public sealed record Profile(string Name, string Transport, JsonObject Outbound, CoreKind Core = CoreKind.Xray) : System.ComponentModel.INotifyPropertyChanged
{
    // Состояние строки отдельно от record: оно не участвует в равенстве профилей.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Profile, ListPresentation> listPresentations = new();
    private sealed class ListPresentation
    {
        public string Status = "Не проверен";
        public System.ComponentModel.PropertyChangedEventHandler? Handlers;
    }
    private ListPresentation Presentation => listPresentations.GetValue(this, static _ => new());
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
    {
        add => Presentation.Handlers += value;
        remove => Presentation.Handlers -= value;
    }
    public string ListStatus => Presentation.Status;
    public void SetListStatus(string value)
    {
        var presentation = Presentation;
        if (presentation.Status == value) return;
        presentation.Status = value;
        presentation.Handlers?.Invoke(this, new(nameof(ListStatus)));
    }
    public ProfileSource Source { get; init; } = ProfileSource.External;
    // Группа (вкладка) узла: подписка или «Вручную» (ProfileGroup). В прежних снимках её нет.
    public string Group { get; init; } = "";
    public ServerCountry Country => CountryCatalog.Resolve(Name);
    public string DisplayName => ServerLabels.DisplayName(Name);
    public bool IsService => Source == ProfileSource.Service;
    // Для шаблона списка: WPF проще отдать готовую видимость, чем заводить конвертер.
    public System.Windows.Visibility ExternalBadgeVisibility =>
        IsService ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
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
        if (ShareLinks.IsLink(input)) return ShareLinks.Parse(input);
        try
        {
            var root = JsonNode.Parse(input, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new UserError("Нужна ссылка vless://, trojan://, hy2:// или JSON-объект Xray / sing-box.");
            JsonObject outbound;
            if (root["outbounds"] is JsonArray outbounds)
            {
                var candidates = outbounds.OfType<JsonObject>().Where(n => IsXrayProtocol(n) || SingBoxProfiles.IsSupported(n)).ToList();
                if (candidates.Count != 1) throw new UserError("В конфигурации должен быть ровно один VLESS/Trojan outbound. Экспортируй нужное подключение отдельно.");
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
        var protocol = outbound["protocol"]?.GetValue<string>();
        if (protocol is not ("vless" or "trojan"))
            throw new UserError("В этой сборке через Xray поддерживаются VLESS и Trojan.");
        if (outbound["proxySettings"] is not null || outbound["sendThrough"] is not null)
            throw new UserError("Профиль с цепочкой или привязкой интерфейса пока не поддерживается.");
        var settings = outbound["settings"] as JsonObject ?? throw new UserError("В профиле нет settings.");
        if (protocol == "vless") ValidateVless(settings); else ValidateTrojan(settings);
        var stream = outbound["streamSettings"] as JsonObject ?? throw new UserError("В профиле нет streamSettings.");
        var security = stream["security"]?.GetValue<string>() ?? "none";
        // Keep this first build on encrypted public transports, including for optional VLESS Encryption.
        if (security is not ("tls" or "reality")) throw new UserError("Первая сборка принимает профили с TLS или REALITY.");
        if (HasInsecureTls(stream))
            throw new UserError("Профиль отключает проверку TLS-сертификата. Используйте профиль с проверкой сертификата.");
        var network = stream["method"]?.GetValue<string>() ?? stream["network"]?.GetValue<string>() ?? "tcp";
        network = network.ToLowerInvariant() switch { "websocket" => "ws", "splithttp" => "xhttp", var n => n };
        if (network is not ("tcp" or "raw" or "xhttp" or "ws" or "grpc" or "httpupgrade"))
            throw new UserError("Этот транспорт пока не поддерживается.");
        var sockopt = stream["sockopt"] as JsonObject;
        if (sockopt?["dialerProxy"] is not null) throw new UserError("Профиль с дополнительным dialerProxy пока не поддерживается.");
        outbound["tag"] = "tunnel";
        var label = (protocol == "trojan" ? "TROJAN · " : "") + network.ToUpperInvariant() + " / " + security.ToUpperInvariant();
        return new Profile(SafeName(name), label, outbound);
    }

    // Server entry of a validated Xray outbound: vnext[0] for VLESS, servers[0] for Trojan.
    internal static JsonObject XrayEndpoint(JsonObject outbound) => (outbound["protocol"]?.GetValue<string>() == "trojan"
        ? outbound["settings"]!["servers"]![0] : outbound["settings"]!["vnext"]![0])!.AsObject();

    internal static bool IsXrayProtocol(JsonObject outbound) => outbound["protocol"]?.GetValue<string>() is "vless" or "trojan";

    private static void ValidateVless(JsonObject settings)
    {
        // Xray 26.9.9 uses vnext. Newer flat address/id syntax is intentionally not guessed.
        var vnext = settings["vnext"] as JsonArray;
        if (vnext?.Count != 1 || vnext[0] is not JsonObject peer || peer["users"] is not JsonArray users || users.Count != 1)
            throw new UserError("Для Xray 26.9.9 нужны settings.vnext с одним сервером и одним пользователем.");
        ValidateEndpoint(peer);
        var user = users[0] as JsonObject ?? throw new UserError("Не указан пользователь VLESS.");
        if (string.IsNullOrWhiteSpace(user["id"]?.GetValue<string>())) throw new UserError("Не указан идентификатор VLESS.");
    }

    private static void ValidateTrojan(JsonObject settings)
    {
        if (settings["servers"] is not JsonArray servers || servers.Count != 1 || servers[0] is not JsonObject server)
            throw new UserError("Для Trojan нужен settings.servers с одним сервером.");
        ValidateEndpoint(server);
        if (string.IsNullOrEmpty(server["password"]?.GetValue<string>())) throw new UserError("Не указан пароль Trojan.");
    }

    private static void ValidateEndpoint(JsonObject peer)
    {
        var address = peer["address"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace)) throw new UserError("Не указан корректный адрес сервера.");
        var port = peer["port"]?.GetValue<int>() ?? 0;
        if (port is < 1 or > 65535) throw new UserError("Порт сервера должен быть от 1 до 65535.");
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
                // UDP нужен и локальному режиму, и системному: исходящее звено системного
                // туннеля — этот же локальный SOCKS. Без него игры и звонки не работают.
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true } }),
            ["outbounds"] = new JsonArray(copy),
            ["routing"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("local"), ["outboundTag"] = "tunnel" }) }
        };
    }

    internal static string SafeName(string name)
    {
        var safe = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "VLESS-профиль" : safe;
    }
}

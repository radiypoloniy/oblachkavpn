using System.Text.Json.Nodes;

namespace Oblachka;

// Отпечаток TLS (uTLS) для сторонних ссылок без своего «fp» — из «Для продвинутых». Подставляется
// при сборке конфигурации ядра, сохранённый профиль не меняется: его хеш и замеры остаются прежними.
// Профили сервиса не трогаются — у их узлов отпечаток задан.
public static class Utls
{
    public static readonly string[] Names = ["chrome", "firefox", "edge", "safari", "ios", "android", "random"];
    public static string? Default { get; set; }

    public static bool Valid(string? name) => name is null || Names.Contains(name);

    // Профиль с отпечатком по умолчанию, если он нужен; иначе тот же профиль.
    public static Profile For(Profile profile)
    {
        if (Default is not { } fp || profile.IsService) return profile;
        var copy = (JsonObject)profile.Outbound.DeepClone();
        return Apply(copy, profile.Core, fp) ? profile with { Outbound = copy } : profile;
    }

    public static bool Apply(JsonObject outbound, CoreKind core, string fp)
    {
        if (core == CoreKind.Xray)
        {
            if (outbound["streamSettings"] is not JsonObject stream || stream["security"]?.GetValue<string>() != "tls") return false;
            var tls = stream["tlsSettings"] as JsonObject ?? new JsonObject();
            if (tls["fingerprint"] is not null) return false;
            tls["fingerprint"] = fp; stream["tlsSettings"] = tls;
            return true;
        }
        if (outbound["tls"] is not JsonObject box || box["enabled"]?.GetValue<bool>() != true || box["utls"] is not null || box["reality"] is not null) return false;
        if (outbound["type"]?.GetValue<string>() is "hysteria2" or "tuic") return false;
        box["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
        return true;
    }
}

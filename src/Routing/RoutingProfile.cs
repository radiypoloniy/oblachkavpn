using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oblachka;

// Маршрутизация по спискам: какие сайты и адреса идут напрямую, через VPN или блокируются.
// Источник — сервис (заголовок подписки «routing: happ://routing/add/<base64>», формат Happ,
// его отдаёт Remnawave) или встроенный набор «Российские сайты напрямую».
//
// Записи сайтов — синтаксис Xray: domain:x (домен с поддоменами), full:x (точно), keyword:x,
// regexp:x, geosite:категория; записи адресов — CIDR, IP или geoip:категория. Категории
// разворачиваются из баз geosite.dat / geoip.dat (GeoDat), которые скачивает окно.
public sealed record RoutingProfile(string Name,
    string[] DirectSites, string[] ProxySites, string[] BlockSites,
    string[] DirectIp, string[] ProxyIp, string[] BlockIp)
{
    public string? GeoipUrl { get; init; }
    public string? GeositeUrl { get; init; }
    // true — всё, что не попало в списки, идёт через VPN; false — напрямую.
    public bool GlobalProxy { get; init; } = true;
    // Порядок применения списков: у Happ «block-direct-proxy» по умолчанию.
    public string Order { get; init; } = "block-direct-proxy";

    public const int MaxEntries = 500;
    public const string DefaultGeosite = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat";
    public const string DefaultGeoip = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat";

    // Госуслуги, банки, маркетплейсы и почти весь рунет: зоны .ru, .рф, .su и категория рунета.
    public static RoutingProfile RussiaDirect => new("Российские сайты напрямую",
        ["domain:ru", "domain:xn--p1ai", "domain:su", "geosite:category-ru"], [], [], [], [], [])
    { GeositeUrl = DefaultGeosite };

    // Свои списки из «Для продвинутых»: записи того же вида, что у сервиса; адреса и geoip —
    // в списки адресов, остальное — в списки сайтов. Порядок общий для всех наборов: блок, напрямую, VPN.
    public static RoutingProfile? Custom(string[] direct, string[] proxy, string[] block)
    {
        static bool Ip(string entry) => NormalizeIp(entry) is not null;
        return new RoutingProfile("Свои списки", direct.Where(e => !Ip(e)).ToArray(), proxy.Where(e => !Ip(e)).ToArray(), block.Where(e => !Ip(e)).ToArray(),
            direct.Where(Ip).ToArray(), proxy.Where(Ip).ToArray(), block.Where(Ip).ToArray()).Normalized();
    }

    public IEnumerable<string> GeositeTags => DirectSites.Concat(ProxySites).Concat(BlockSites)
        .Where(e => e.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)).Select(e => Tag(e[8..])).Where(t => t.Length > 0).Distinct();
    public IEnumerable<string> GeoipTags => DirectIp.Concat(ProxyIp).Concat(BlockIp)
        .Where(e => e.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)).Select(e => Tag(e[6..])).Where(t => t.Length > 0).Distinct();
    public bool HasDirect => DirectSites.Length + DirectIp.Length > 0 || !GlobalProxy;
    public int RuleCount => DirectSites.Length + ProxySites.Length + BlockSites.Length + DirectIp.Length + ProxyIp.Length + BlockIp.Length;

    // «category-ru@!cn» — атрибуты не поддерживаются, берётся сама категория.
    internal static string Tag(string value)
    {
        var tag = value.Split('@', 2)[0].Trim().ToLowerInvariant();
        return Regex.IsMatch(tag, "^[a-z0-9][a-z0-9!._-]{0,63}$") && !tag.StartsWith('!') ? tag : "";
    }

    // «happ://routing/add/<base64 JSON>» или «happ://routing/onadd/…». Чужие поля пропускаются,
    // записи проверяются; невалидная запись отбрасывается, а не роняет весь профиль.
    public static RoutingProfile? ParseHapp(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 64 * 1024) return null;
        var match = Regex.Match(header.Trim(), "^happ://routing/(?:add|onadd)/([A-Za-z0-9+/=_-]+)$");
        if (!match.Success) return null;
        JsonObject? json;
        try
        {
            var text = match.Groups[1].Value.Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            json = JsonNode.Parse(Convert.FromBase64String(text)) as JsonObject;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
        if (json is null) return null;
        string? Text(string key) => json.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        string[] List(string key) => json.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value is JsonArray a
            ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().Take(MaxEntries).ToArray() : [];
        var profile = new RoutingProfile(SubscriptionMetadata.Clean(Text("Name")) ?? "Маршрутизация сервиса",
            List("DirectSites"), List("ProxySites"), List("BlockSites"), List("DirectIp"), List("ProxyIp"), List("BlockIp"))
        {
            GeoipUrl = HttpsUrl(Text("Geoipurl")), GeositeUrl = HttpsUrl(Text("Geositeurl")),
            GlobalProxy = !string.Equals(Text("GlobalProxy"), "false", StringComparison.OrdinalIgnoreCase),
            Order = Text("RouteOrder") is { } order && ValidOrder(order) ? order : "block-direct-proxy"
        };
        return profile.Normalized();
    }

    private static string? HttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;

    private static bool ValidOrder(string order)
    {
        var parts = order.ToLowerInvariant().Split('-');
        return parts.Length == 3 && parts.OrderBy(p => p).SequenceEqual(new[] { "block", "direct", "proxy" });
    }

    // Только допустимые записи; повторы убираются. Пустой профиль — null.
    public RoutingProfile? Normalized()
    {
        static string[] Sites(IEnumerable<string> list) => list.Select(NormalizeSite).OfType<string>().Distinct().Take(MaxEntries).ToArray();
        static string[] Ips(IEnumerable<string> list) => list.Select(NormalizeIp).OfType<string>().Distinct().Take(MaxEntries).ToArray();
        var result = this with
        {
            Name = SubscriptionMetadata.Clean(Name) ?? "Маршрутизация",
            DirectSites = Sites(DirectSites), ProxySites = Sites(ProxySites), BlockSites = Sites(BlockSites),
            DirectIp = Ips(DirectIp), ProxyIp = Ips(ProxyIp), BlockIp = Ips(BlockIp),
            Order = ValidOrder(Order) ? Order.ToLowerInvariant() : "block-direct-proxy",
            GeoipUrl = HttpsUrl(GeoipUrl), GeositeUrl = HttpsUrl(GeositeUrl)
        };
        return result.RuleCount == 0 && result.GlobalProxy ? null : result;
    }

    private static readonly Regex DomainLike = new("^[a-z0-9*._-]{1,253}$", RegexOptions.CultureInvariant);

    internal static string? NormalizeSite(string entry)
    {
        var text = entry.Trim();
        if (text.Length is 0 or > 256) return null;
        var colon = text.IndexOf(':');
        var kind = colon > 0 ? text[..colon].ToLowerInvariant() : "keyword";
        var value = colon > 0 ? text[(colon + 1)..].Trim() : text;
        switch (kind)
        {
            case "domain" or "full" or "keyword":
                value = value.TrimStart('.').TrimEnd('.').ToLowerInvariant();
                try { value = new System.Globalization.IdnMapping().GetAscii(value); } catch (ArgumentException) { return null; }
                return value.Length > 0 && DomainLike.IsMatch(value) && !value.Contains('*') ? kind + ":" + value : null;
            case "regexp":
                // Ядра понимают синтаксис RE2: без забеганий вперёд и обратных ссылок.
                if (value.Length > 200 || value.Contains("(?=") || value.Contains("(?!") || value.Contains("(?<") || Regex.IsMatch(value, @"\\[1-9]")) return null;
                try { _ = new Regex(value, RegexOptions.None, TimeSpan.FromMilliseconds(50)); } catch (ArgumentException) { return null; }
                return "regexp:" + value;
            case "geosite":
                return Tag(value) is { Length: > 0 } tag ? "geosite:" + tag : null;
            default: return null;
        }
    }

    internal static string? NormalizeIp(string entry)
    {
        var text = entry.Trim();
        if (text.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
            return Tag(text[6..]) is { Length: > 0 } tag ? "geoip:" + tag : null;
        var parts = text.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var ip)) return null;
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > max)) return null;
        return ip + "/" + prefix;
    }
}

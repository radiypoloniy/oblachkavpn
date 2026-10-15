using System.Text.Json.Nodes;

namespace Oblachka;

// Списки одного действия в виде, понятном обоим ядрам.
public sealed class RouteMatchers
{
    public HashSet<string> Full { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Suffix { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Keyword { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Regex { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Cidr { get; } = new(StringComparer.Ordinal);
    public bool HasSites => Full.Count + Suffix.Count + Keyword.Count + Regex.Count > 0;
    public bool IsEmpty => !HasSites && Cidr.Count == 0;
}

// Маршрутизация, развёрнутая для ядра: категории баз уже превращены в домены и диапазоны.
// Missing — категории, которых нет в скачанных базах или для которых базы не скачаны.
public sealed record CompiledRouting(RouteMatchers Direct, RouteMatchers Proxy, RouteMatchers Block, bool GlobalProxy, string[] Order, string[] Missing)
{
    public bool HasDirect => !Direct.IsEmpty || !GlobalProxy;
    public bool IsEmpty => Direct.IsEmpty && Proxy.IsEmpty && Block.IsEmpty && GlobalProxy;

    public static CompiledRouting? Compile(IEnumerable<RoutingProfile> profiles, GeoStore geo)
    {
        var list = profiles.ToArray();
        if (list.Length == 0) return null;
        var siteTags = list.SelectMany(p => p.GeositeTags).Distinct().ToArray();
        var ipTags = list.SelectMany(p => p.GeoipTags).Distinct().ToArray();
        var sites = siteTags.Length > 0 ? geo.Sites(siteTags) : new();
        var ips = ipTags.Length > 0 ? geo.Ips(ipTags) : new();
        var missing = siteTags.Where(t => !sites.ContainsKey(t)).Select(t => "geosite:" + t)
            .Concat(ipTags.Where(t => !ips.ContainsKey(t)).Select(t => "geoip:" + t)).ToArray();
        var direct = new RouteMatchers(); var proxy = new RouteMatchers(); var block = new RouteMatchers();
        foreach (var profile in list)
        {
            AddSites(direct, profile.DirectSites, sites); AddSites(proxy, profile.ProxySites, sites); AddSites(block, profile.BlockSites, sites);
            AddIps(direct, profile.DirectIp, ips); AddIps(proxy, profile.ProxyIp, ips); AddIps(block, profile.BlockIp, ips);
        }
        // Первый профиль задаёт порядок и итог: встроенный набор добавляется к правилам сервиса.
        return new(direct, proxy, block, list.All(p => p.GlobalProxy), list[0].Order.Split('-'), missing);
    }

    private static void AddSites(RouteMatchers into, IEnumerable<string> entries, Dictionary<string, List<GeoDat.SiteEntry>> categories)
    {
        foreach (var entry in entries)
        {
            var colon = entry.IndexOf(':');
            var (kind, value) = (entry[..colon], entry[(colon + 1)..]);
            switch (kind)
            {
                case "domain": into.Suffix.Add(value); break;
                case "full": into.Full.Add(value); break;
                case "keyword": into.Keyword.Add(value); break;
                case "regexp": into.Regex.Add(value); break;
                case "geosite" when categories.TryGetValue(value, out var domains):
                    foreach (var domain in domains)
                    {
                        if (RoutingProfile.NormalizeSite(Prefix(domain.Kind) + domain.Value) is not { } normalized) continue;
                        var (k, v) = (normalized[..normalized.IndexOf(':')], normalized[(normalized.IndexOf(':') + 1)..]);
                        (k switch { "domain" => into.Suffix, "full" => into.Full, "keyword" => into.Keyword, _ => into.Regex }).Add(v);
                    }
                    break;
            }
        }
    }

    private static string Prefix(GeoDat.DomainKind kind) => kind switch
    {
        GeoDat.DomainKind.Suffix => "domain:", GeoDat.DomainKind.Full => "full:", GeoDat.DomainKind.Regex => "regexp:", _ => "keyword:"
    };

    private static void AddIps(RouteMatchers into, IEnumerable<string> entries, Dictionary<string, List<string>> categories)
    {
        foreach (var entry in entries)
        {
            if (!entry.StartsWith("geoip:", StringComparison.Ordinal)) into.Cidr.Add(entry);
            else if (categories.TryGetValue(entry[6..], out var ranges)) foreach (var range in ranges) into.Cidr.Add(range);
        }
    }

    // Правила Xray: домены одним правилом, адреса другим; порядок действий — как задал сервис.
    public JsonArray XrayRules(string tunnel, string direct, string block)
    {
        var rules = new JsonArray();
        foreach (var action in Order)
        {
            var (set, outbound) = action switch { "block" => (Block, block), "direct" => (Direct, direct), _ => (Proxy, tunnel) };
            if (set.HasSites)
                rules.Add(new JsonObject
                {
                    ["type"] = "field", ["outboundTag"] = outbound,
                    ["domain"] = new JsonArray(set.Suffix.Select(d => (JsonNode?)("domain:" + d)).Concat(set.Full.Select(d => (JsonNode?)("full:" + d)))
                        .Concat(set.Keyword.Select(d => (JsonNode?)("keyword:" + d))).Concat(set.Regex.Select(d => (JsonNode?)("regexp:" + d))).ToArray())
                });
            if (set.Cidr.Count > 0)
                rules.Add(new JsonObject { ["type"] = "field", ["outboundTag"] = outbound, ["ip"] = new JsonArray(set.Cidr.Select(c => (JsonNode?)c).ToArray()) });
        }
        return rules;
    }

    // Правила sing-box в том же порядке. «Блок» — действие reject, а не выход.
    public JsonArray SingBoxRules(string tunnel, string direct)
    {
        var rules = new JsonArray();
        foreach (var action in Order)
        {
            var set = action switch { "block" => Block, "direct" => Direct, _ => Proxy };
            void Add(JsonObject match)
            {
                if (action == "block") match["action"] = "reject"; else match["outbound"] = action == "direct" ? direct : tunnel;
                rules.Add(match);
            }
            if (set.HasSites)
            {
                var match = new JsonObject();
                if (set.Suffix.Count > 0) match["domain_suffix"] = new JsonArray(set.Suffix.Select(d => (JsonNode?)d).ToArray());
                if (set.Full.Count > 0) match["domain"] = new JsonArray(set.Full.Select(d => (JsonNode?)d).ToArray());
                if (set.Keyword.Count > 0) match["domain_keyword"] = new JsonArray(set.Keyword.Select(d => (JsonNode?)d).ToArray());
                if (set.Regex.Count > 0) match["domain_regex"] = new JsonArray(set.Regex.Select(d => (JsonNode?)d).ToArray());
                Add(match);
            }
            if (set.Cidr.Count > 0) Add(new JsonObject { ["ip_cidr"] = new JsonArray(set.Cidr.Select(c => (JsonNode?)c).ToArray()) });
        }
        return rules;
    }

    // «Куда пойдёт сайт?» с учётом списков: действие для домена или null, если списки молчат.
    public SiteAction? Decide(string domain)
    {
        foreach (var action in Order)
        {
            var set = action switch { "block" => Block, "direct" => Direct, _ => Proxy };
            if (set.Full.Contains(domain) || set.Keyword.Any(domain.Contains) ||
                set.Suffix.Any(s => domain == s || domain.EndsWith("." + s, StringComparison.Ordinal)))
                return action switch { "block" => SiteAction.Block, "direct" => SiteAction.Direct, _ => SiteAction.Tunnel };
        }
        return GlobalProxy ? null : SiteAction.Direct;
    }
}

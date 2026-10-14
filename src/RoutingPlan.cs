using System.Globalization;
using System.Text.Json.Nodes;

namespace Oblachka;

public enum SiteAction { Tunnel, Direct, Block }
public sealed record SiteRule(string Domain, SiteAction Action)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => Action switch { SiteAction.Tunnel => "Через VPN", SiteAction.Direct => "Напрямую", _ => "Блокировать" };
    public override string ToString() => Domain + " — " + Label;
}

// Правила сайтов в ядре обычного режима (Engine.SiteRules). В системном режиме те же
// правила исполняет ядро туннеля (SingBoxTunConfig), а здесь список пуст.
public static class RoutingPlan
{
    public static SiteRule Create(string domain, SiteAction action)
    {
        domain = domain.Trim().TrimEnd('.');
        try { domain = new IdnMapping().GetAscii(domain).ToLowerInvariant(); }
        catch (ArgumentException) { throw new UserError("Укажите домен сайта, например example.com."); }
        if (!Enum.IsDefined(action) || domain.Length is < 3 or > 253 || !domain.Contains('.') ||
            System.Net.IPAddress.TryParse(domain, out _) || domain.Split('.').Any(p => p.Length is < 1 or > 63 || p.StartsWith('-') || p.EndsWith('-') || p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
            throw new UserError("Нужен домен без https://, пути, порта и звёздочек.");
        return new(domain, action);
    }
    public static void Validate(IReadOnlyList<SiteRule>? rules)
    {
        if (rules is null || rules.Count > 100) throw new UserError("Допускается до 100 правил сайтов.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
            if (rule is null || Create(rule.Domain, rule.Action).Domain != rule.Domain || !seen.Add(rule.Domain)) throw new UserError("Правила повреждены или повторяются.");
    }
    // routing — списки сервиса и встроенные наборы (CompiledRouting). Идут после правил
    // пользователя: явное правило сайта сильнее списка.
    public static JsonObject Compile(CoreAdapter adapter, Profile profile, int port, IReadOnlyList<SiteRule> rules, CompiledRouting? routing = null)
    {
        Validate(rules);
        if (profile.Core != adapter.Kind) throw new UserError("Ядро не соответствует профилю.");
        var config = adapter.RuntimeConfig(profile.Outbound, port);
        var outbounds = (JsonArray)config["outbounds"]!;
        var xray = adapter.Kind == CoreKind.Xray;
        var tunnelTag = outbounds[0]!["tag"]!.GetValue<string>();
        if (routing is { IsEmpty: true }) routing = null;
        if (rules.Any(r => r.Action == SiteAction.Direct) || routing?.HasDirect == true)
            outbounds.Add(xray ? new JsonObject { ["tag"] = "user-direct", ["protocol"] = "freedom" } : new JsonObject { ["tag"] = "user-direct", ["type"] = "direct" });
        if (xray && (rules.Any(r => r.Action == SiteAction.Block) || routing?.Block.IsEmpty == false))
            outbounds.Add(new JsonObject { ["tag"] = "user-block", ["protocol"] = "blackhole" });
        // В системном режиме ядро получает из туннеля соединения по IP: имя сайта берётся из
        // TLS/HTTP/QUIC (sniffing), иначе списки доменов не сработали бы. Маршрут — по имени,
        // соединение — на исходный адрес (routeOnly), чтобы не делать лишний DNS.
        if (routing is not null || rules.Count > 0)
        {
            if (xray) config["inbounds"]![0]!["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic"), ["routeOnly"] = true };
            else ((JsonArray)config["route"]!["rules"]!).Insert(0, new JsonObject { ["action"] = "sniff" });
        }
        var compiled = new JsonArray();
        foreach (var rule in rules.OrderByDescending(r => r.Domain.Count(c => c == '.')))
        {
            var destination = rule.Action == SiteAction.Direct ? "user-direct" : rule.Action == SiteAction.Block ? "user-block" : tunnelTag;
            compiled.Add(xray
                ? new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("domain:" + rule.Domain), ["outboundTag"] = destination }
                : rule.Action == SiteAction.Block
                    ? new JsonObject { ["domain_suffix"] = new JsonArray(rule.Domain), ["action"] = "reject" }
                    : new JsonObject { ["domain_suffix"] = new JsonArray(rule.Domain), ["outbound"] = destination });
        }
        if (routing is not null)
            foreach (var node in xray ? routing.XrayRules(tunnelTag, "user-direct", "user-block") : routing.SingBoxRules(tunnelTag, "user-direct"))
                compiled.Add(node!.DeepClone());
        var route = (JsonObject)config[xray ? "routing" : "route"]!;
        // Правила ядра идут впереди пользовательских: для sing-box это перехват DNS.
        var original = (JsonArray)route["rules"]!;
        if (xray)
        {
            foreach (var node in original) compiled.Add(node!.DeepClone());
            // Списки без «всё остальное через VPN»: последнее правило уводит прочее напрямую.
            if (routing is { GlobalProxy: false }) compiled[^1]!["outboundTag"] = "user-direct";
            route["rules"] = compiled;
        }
        else
        {
            foreach (var node in compiled) original.Add(node!.DeepClone());
            if (routing is { GlobalProxy: false }) route["final"] = "user-direct";
        }
        return config;
    }
}

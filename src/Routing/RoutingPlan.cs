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
    private static JsonObject DirectOutbound(bool xray, CoreDirectEntry? entry) => entry is null
        ? xray ? new JsonObject { ["tag"] = "user-direct", ["protocol"] = "freedom" } : new JsonObject { ["tag"] = "user-direct", ["type"] = "direct" }
        : xray
            ? new JsonObject
            {
                ["tag"] = "user-direct", ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject
                {
                    ["address"] = "127.0.0.1", ["port"] = entry.Port,
                    ["users"] = new JsonArray(new JsonObject { ["user"] = entry.User, ["pass"] = entry.Password })
                }) }
            }
            : new JsonObject
            {
                ["tag"] = "user-direct", ["type"] = "socks", ["server"] = "127.0.0.1", ["server_port"] = entry.Port,
                ["version"] = "5", ["username"] = entry.User, ["password"] = entry.Password
            };

    // Цель — домен, подсеть или порт (SiteTarget).
    public static SiteRule Create(string domain, SiteAction action)
    {
        if (!Enum.IsDefined(action)) throw new UserError("Неизвестное действие правила.");
        if (SiteTarget.TryNormalize(domain) is { } target) return new(target, action);
        domain = domain.Trim().TrimEnd('.');
        try { domain = new IdnMapping().GetAscii(domain).ToLowerInvariant(); }
        catch (ArgumentException) { throw new UserError("Укажите домен сайта, например example.com."); }
        if (!Enum.IsDefined(action) || domain.Length is < 3 or > 253 || !domain.Contains('.') ||
            System.Net.IPAddress.TryParse(domain, out _) || domain.Split('.').Any(p => p.Length is < 1 or > 63 || p.StartsWith('-') || p.EndsWith('-') || p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
            throw new UserError("Нужен домен (example.com), подсеть (10.20.0.0/16) или порт (port:25) — без https://, пути и звёздочек.");
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
    // directEntry — режим «Весь компьютер»: «напрямую» ядро отдаёт на локальный вход ядра туннеля
    // (CoreDirectEntry), иначе его прямое соединение снова попало бы в туннель.
    public static JsonObject Compile(CoreAdapter adapter, Profile profile, int port, IReadOnlyList<SiteRule> rules, CompiledRouting? routing = null,
        CoreDirectEntry? directEntry = null)
    {
        Validate(rules);
        if (profile.Core != adapter.Kind) throw new UserError("Ядро не соответствует профилю.");
        // Бесплатный чужой узел: заслоны ForeignGuard впереди правил человека.
        if (profile.IsFree) rules = ForeignGuard.With(rules, 100);
        var config = adapter.RuntimeConfig(Utls.For(profile).Outbound, port);
        var outbounds = (JsonArray)config["outbounds"]!;
        var xray = adapter.Kind == CoreKind.Xray;
        // Метка туннеля одна у всех ядер (CoreAdapter); у WireGuard он в endpoints, а не первым в outbounds.
        const string tunnelTag = "tunnel";
        if (routing is { IsEmpty: true }) routing = null;
        if (rules.Any(r => r.Action == SiteAction.Direct) || routing?.HasDirect == true)
            outbounds.Add(DirectOutbound(xray, directEntry));
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
        foreach (var rule in rules.OrderByDescending(r => SiteTarget.Order(r.Domain)))
        {
            var destination = rule.Action == SiteAction.Direct ? "user-direct" : rule.Action == SiteAction.Block ? "user-block" : tunnelTag;
            var match = xray ? SiteTarget.Xray(rule.Domain) : SiteTarget.SingBox(rule.Domain);
            if (xray) { match["type"] = "field"; match["outboundTag"] = destination; }
            else if (rule.Action == SiteAction.Block) match["action"] = "reject";
            else match["outbound"] = destination;
            compiled.Add(match);
        }
        if (profile.IsFree) compiled.Insert(0, xray ? ForeignGuard.XrayHttpRule("user-block") : ForeignGuard.SingBoxHttpRule());
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

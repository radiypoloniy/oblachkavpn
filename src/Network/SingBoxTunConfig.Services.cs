using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SingBoxTunConfig
{
    internal const string MainOn = "Rule", MainOff = "StrictRoutes", MainBlocked = "StrictRoutesLockdown";

    private static JsonObject ExactPrograms(IEnumerable<ProgramRule> files) => new()
    {
        ["process_path_regex"] = new JsonArray(files.Select(file => (JsonNode?)JsonValue.Create(
            "(?i)^" + ProgramRoutingPlan.Re2(file.FilePath!) + "$")).ToArray())
    };

    private static JsonObject RejectWhenMainOff(JsonObject match)
    {
        var reject = match.DeepClone().AsObject();
        reject["clash_mode"] = MainOff;
        reject["action"] = "reject";
        return reject;
    }

    private static void AddServiceRoutes(JsonObject config, IReadOnlyList<PinnedServiceRoute> services)
    {
        var rules = config["route"]!["rules"]!.AsArray();
        static JsonObject Pin(JsonObject match, int? exit)
        {
            match["inbound"] = new JsonArray("system-tun");
            return exit is int index ? Route(match, SiteAction.Tunnel, DirectTag(index)) : Route(match, SiteAction.Block, ServerSelector);
        }
        // An exact executable can claim all its connections. For traffic
        // from shared hosts (browsers, IDEs) the most specific domain wins.
        foreach (var service in services)
            foreach (var file in service.Files)
                rules.Add(Pin(ProgramRoutingPlan.Match(file), service.ExitIndex));
        foreach (var target in services.SelectMany(s => s.Domains.Select(d => (Domain: d, s.ExitIndex)))
                     .OrderByDescending(d => SiteTarget.Order(d.Domain)))
            rules.Add(Pin(SiteTarget.SingBox(target.Domain), target.ExitIndex));
    }

    private static void AddPinnedDns(JsonObject config, IReadOnlyList<PinnedServiceRoute> services, bool canPauseMain)
    {
        if (services.Count == 0) return;
        var dns = config["dns"]!.AsObject();
        var servers = dns["servers"]!.AsArray();
        var rules = new JsonArray();
        var tags = new HashSet<string>(StringComparer.Ordinal);
        string AddResolver(string tag, string outbound)
        {
            if (tags.Add(tag))
            {
                var resolver = new JsonObject
                {
                    ["type"] = "https", ["tag"] = tag, ["server"] = "1.1.1.1", ["server_port"] = 443, ["path"] = "/dns-query",
                    ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" }
                };
                if (outbound != UserDirect) resolver["detour"] = outbound;
                servers.Add(resolver);
            }
            return tag;
        }
        void Pin(JsonObject match, int? exit)
        {
            if (exit is int index) match["server"] = AddResolver("pinned-dns-" + index, DirectTag(index));
            else match["action"] = "reject";
            rules.Add(match);
        }
        foreach (var route in services)
            if (route.Files.Length > 0)
                Pin(ExactPrograms(route.Files), route.ExitIndex);
        foreach (var target in services.SelectMany(s => s.Domains.Select(d => (Domain: d, s.ExitIndex)))
                     .OrderByDescending(d => SiteTarget.Order(d.Domain)))
            Pin(new() { ["domain_suffix"] = new JsonArray(target.Domain) }, target.ExitIndex);
        if (canPauseMain)
        {
            var ordinary = AddResolver("ordinary-dns", UserDirect);
            config["outbounds"]!.AsArray().OfType<JsonObject>()
                .Single(outbound => (string?)outbound["tag"] == UserDirect)["domain_resolver"] = ordinary;
            rules.Add(new JsonObject { ["clash_mode"] = MainBlocked, ["action"] = "reject" });
            rules.Add(new JsonObject { ["clash_mode"] = MainOff, ["server"] = ordinary });
        }
        if (dns["rules"] is JsonArray existing)
            foreach (var rule in existing) rules.Add(rule!.DeepClone());
        dns["rules"] = rules;
    }
    private static void AddRules(JsonObject config, ValidatedNetworkLease lease, string? programSets, string tunnel)
    {
        var rules = (JsonArray)config["route"]!["rules"]!;
        if (lease.DirectExit) ((JsonArray)config["outbounds"]!).Add(new JsonObject { ["type"] = "direct", ["tag"] = UserDirect });
        // Соединения для «Сейчас в сети»: только 127.0.0.1 и только с секретом этой сессии.
        CoreProgramsConfig.Apply(config, lease.Stats);
        // Прямой трафик ядра окна (списки маршрутизации) приходит на свой локальный вход с паролем
        // сессии и уходит мимо туннеля. Правило — самое первое, раньше перехвата DNS: этот трафик
        // ядро уже разобрало, и возвращать его ядру нельзя — получилась бы петля.
        if (lease.CoreDirect is { } entry && lease.ServiceRoutes.Count == 0)
        {
            ((JsonArray)config["inbounds"]!).Add(new JsonObject
            {
                ["type"] = "socks", ["tag"] = CoreDirectInbound, ["listen"] = "127.0.0.1", ["listen_port"] = entry.Port,
                ["users"] = new JsonArray(new JsonObject { ["username"] = entry.User, ["password"] = entry.Password })
            });
            rules.Insert(0, new JsonObject { ["inbound"] = new JsonArray(CoreDirectInbound), ["outbound"] = UserDirect });
        }
        // Локальная сеть — раньше правил сайтов и программ: принтер и NAS не уходят на сервер.
        if (lease.LocalNetwork && lease.ServiceRoutes.Count == 0) rules.Add(LocalNetworkRule());
        // «QUIC через TCP» — после локальной сети и раньше правил: отказ сразу (ICMP), браузер
        // без ожидания переходит на HTTP/2. Касается всех сайтов, и прямых тоже.
        if (lease.QuicOverTcp) rules.Add(new JsonObject { ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });
        // Бесплатный чужой узел: незашифрованный HTTP закрыт для всего (ForeignGuard), прямые сайты тоже — так проще и строже.
        if (lease.ForeignNode) rules.Add(ForeignGuard.SingBoxHttpRule());
        // Имя сайта ядро узнаёт из TLS/QUIC. С файлами правил сайт может появиться в правилах на ходу,
        // поэтому имя нужно всегда, а не только когда правила сайтов были при подключении.
        if (lease.SiteRules.Count > 0 || lease.ServiceRoutes.Any(r => r.Domains.Length > 0) || lease.Lists is not null || programSets is not null)
            rules.Add(new JsonObject { ["action"] = "sniff" });
        AddServiceRoutes(config, lease.ServiceRoutes);
        if (lease.ServiceRoutes.Any(route => route.Files.Length > 0))
            rules.Add(new JsonObject { ["type"] = "logical", ["mode"] = "and", ["rules"] = new JsonArray(
                    new JsonObject { ["inbound"] = new JsonArray("system-tun") },
                    new JsonObject { ["process_path_regex"] = new JsonArray(".+"), ["invert"] = true }), ["action"] = "reject" });
        if (lease.CanPauseMain) rules.Add(new JsonObject { ["clash_mode"] = MainBlocked, ["action"] = "reject" });
        JsonObject Guarded(JsonObject match, SiteAction action)
        {
            if (lease.CanPauseMain && action == SiteAction.Tunnel) rules.Add(RejectWhenMainOff(match));
            return Route(match, action == SiteAction.Direct && !lease.HasDirect ? SiteAction.Block : action, tunnel);
        }
        // Прямой выход разрешён защитой, только если «напрямую» было при подключении. Правило
        // «напрямую», добавленное на ходу без него, блокирует программу: мимо VPN без разрешения
        // она не уйдёт, а окно попросит переподключиться.
        // Правила с сайтом для одной программы — в тех же файлах, что правила программ (раньше них);
        // правила сайтов для всех программ — после правил программ, своими файлами (SiteRuleSets).
        if (programSets is not null)
        {
            ProgramRuleSets.AddTo(config, programSets, Guarded);
            SiteRuleSets.AddTo(config, SiteRuleSets.NextTo(programSets), Guarded);
        }
        else
        {
            foreach (var rule in ProgramRoutingPlan.Ordered(lease.ProgramRules))
                rules.Add(Guarded(ProgramRoutingPlan.Match(rule), rule.Action));
            foreach (var rule in lease.SiteRules.OrderByDescending(r => SiteTarget.Order(r.Domain)))
                rules.Add(Guarded(SiteTarget.SingBox(rule.Domain), rule.Action));
        }
        if (lease.LocalNetwork && lease.ServiceRoutes.Count > 0) rules.Add(LocalNetworkRule());
    }

}

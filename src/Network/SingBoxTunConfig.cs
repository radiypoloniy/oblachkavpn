using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Oblachka;

// Prepared for the isolated VM stage only. Validation invokes `check`, never starts the TUN.
public static class SingBoxTunConfig
{
    // Адреса самого туннеля. Их же разрешает kill switch, чтобы трафик, уходящий
    // в туннель, проходил при закрытом исходящем действии брандмауэра. Расхождение
    // этих двух мест закрыло бы туннель наглухо, поэтому значение здесь одно.
    public const string TunHost4 = "172.31.255.1";
    public const string TunHost6 = "fdfe:dcba:9876::1";
    // Резолвер, который ядро прописывает интерфейсу туннеля: следующий адрес сети.
    // Проверено на стенде — Windows получает именно эту пару вместе с интерфейсом.
    // Kill switch разрешает DNS только на неё, остальной DNS запрещает.
    public const string TunResolver4 = "172.31.255.2";
    public const string TunResolver6 = "fdfe:dcba:9876::2";

    // ipv6 — давать ли туннелю IPv6-адрес. Только если у физической сети есть глобальный
    // IPv6 (PhysicalNetwork.HasGlobalIpv6). Иначе программы выбирали IPv6 через туннель,
    // стек туннеля принимал соединение, а прямой выход по правилу «напрямую» в сети без
    // IPv6 срывался: программа получала сброс вместо отказа и на IPv4 не переходила
    // (проверено на стенде: curl -4 проходил, curl по умолчанию — нет). IPv6 мимо туннеля
    // при этом закрыт защитой WFP: он быстро отказывает, и программы уходят на IPv4.
    // localDns и ruleSet — DNS физической сети и файл с сайтами, которые идут мимо VPN (DirectDns):
    // их имена разрешаются там же, куда пойдёт трафик. null — всё через DoH в туннеле.
    // programSets — папка файлов правил программ (ProgramRuleSets): правила меняются без
    // переподключения. null — правила программ прямо в конфигурации (проверка черновика).
    // listsFolder — файлы списков маршрутизации (RoutingLists), если аренда передала их ядру туннеля.
    public static JsonObject Create(NetworkLeaseRequest request, bool ipv6 = true, System.Net.IPAddress? localDns = null, string? ruleSet = null, string? programSets = null,
        string? listsFolder = null)
    {
        var lease = NetworkHelperContracts.Validate(request);
        if (lease.Action != NetworkLeaseAction.Acquire) throw new UserError("TUN требует новую сетевую сессию.");
        // Куда идёт трафик «через VPN»: прямо на сервер через переключатель (одно ядро) или в ядро окна.
        var tunnel = lease.Direct is null ? "local-core" : ServerSelector;
        var exclusions = new JsonArray(lease.Peers.Select(ip => (JsonNode?)JsonValue.Create(
            ip + (ip.AddressFamily == AddressFamily.InterNetwork ? "/32" : "/128"))).ToArray());
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["disabled"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject
                {
                    ["type"] = "https", ["tag"] = "remote-dns", ["server"] = "1.1.1.1", ["server_port"] = 443,
                    ["path"] = "/dns-query", ["detour"] = tunnel,
                    ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" }
                }),
                ["final"] = "remote-dns",
                // У сайтов на CDN срок записи — минуты: без этого каждый истёкший адрес ждал бы
                // запроса DoH через туннель. Истёкшая запись отдаётся сразу, обновляется в фоне;
                // старше 6 часов — запрос заново. Кэш только в памяти ядра: на диск история
                // посещённых сайтов не пишется (store_dns в cache_file — сознательно нет).
                ["cache_capacity"] = 8192,
                ["optimistic"] = new JsonObject { ["enabled"] = true, ["timeout"] = "6h" }
            },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "tun", ["tag"] = "system-tun", ["interface_name"] = "Obl-" + lease.LeaseId.ToString("N")[..8],
                ["address"] = ipv6 ? new JsonArray(TunHost4 + "/30", TunHost6 + "/126") : new JsonArray(TunHost4 + "/30"),
                // gvisor с MTU 9000: на стенде скорости (VM, 17.10.2026) против mixed/1500 скачивание
                // в полтора раза быстрее, задержка под нагрузкой на треть ниже, процессора вдвое меньше.
                // TCP завершается в стеке туннеля, наружу ядро ходит с MTU физической сети.
                ["mtu"] = 9000, ["stack"] = "gvisor", ["auto_route"] = true, ["strict_route"] = true,
                ["dns_mode"] = "hijack", ["route_exclude_address"] = exclusions
            }),
            ["outbounds"] = new JsonArray(new JsonObject
            {
                // Без ограничения network туннель отдаёт локальному ядру и UDP тоже.
                ["type"] = "socks", ["tag"] = "local-core", ["server"] = "127.0.0.1",
                ["server_port"] = lease.LoopbackPort, ["version"] = "5"
            }),
            ["route"] = new JsonObject
            {
                // «Через VPN только выбранное»: в туннель — только правила «через VPN», остальное мимо.
                ["auto_detect_interface"] = true, ["final"] = lease.DirectByDefault ? UserDirect : tunnel,
                // Поиск процесса — системный вызов на каждое соединение; без него правила программ
                // молча не срабатывают. С файлами правил он нужен всегда: правило может появиться
                // на ходу, а включается поиск только при запуске ядра. На стенде скорости его
                // цена в пределах шума. Прямой трафик ядра окна узнаётся по своему входу core-direct.
                ["find_process"] = programSets is not null || lease.ProgramRules.Count > 0,
                // Порядок важен: DNS перехватывается первым правилом и уходит на DoH внутри
                // туннеля. Остальной UDP идёт в туннель наравне с TCP.
                ["rules"] = new JsonArray(
                    new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" })
            }
        };
        AddServers(config, lease);
        AddRules(config, lease, programSets, tunnel);
        AddLists(config, lease, listsFolder, tunnel);
        if (lease.HasDirect && localDns is not null && ruleSet is not null) AddDirectDns(config, localDns, ruleSet);
        if (lease.DirectByDefault && localDns is not null && ruleSet is not null) DnsByDefaultDirect(config, lease);
        return config;
    }

    // Имена из файла правил — DNS физической сети; прямой выход и сам разрешает имена через
    // него: проверка zapret и прямой трафик ядра окна приходят с доменом, а не адресом.
    // Файл правил ядро перечитывает при изменении — список меняется без переподключения.
    private static void AddDirectDns(JsonObject config, System.Net.IPAddress localDns, string ruleSet)
    {
        ((JsonArray)config["dns"]!["servers"]!).Add(new JsonObject
        {
            ["type"] = "udp", ["tag"] = DirectDns.ServerTag, ["server"] = localDns.ToString(), ["server_port"] = 53
        });
        config["dns"]!["rules"] = new JsonArray(new JsonObject { ["rule_set"] = new JsonArray(DirectDns.RuleSetTag), ["server"] = DirectDns.ServerTag });
        var sets = config["route"]!["rule_set"] as JsonArray ?? new JsonArray();
        config["route"]!["rule_set"] = sets;
        sets.Add(new JsonObject { ["type"] = "local", ["tag"] = DirectDns.RuleSetTag, ["format"] = "source", ["path"] = ruleSet });
        foreach (var outbound in ((JsonArray)config["outbounds"]!).OfType<JsonObject>())
            if ((string?)outbound["tag"] == UserDirect) outbound["domain_resolver"] = DirectDns.ServerTag;
    }

    // По умолчанию напрямую — и имена по умолчанию через DNS физической сети: загрузки получают
    // ближние узлы. Сайты «через VPN» разрешаются DoH в туннеле, как и раньше.
    private static void DnsByDefaultDirect(JsonObject config, ValidatedNetworkLease lease)
    {
        config["dns"]!["final"] = DirectDns.ServerTag;
        var tunnel = lease.SiteRules.Where(r => r.Action == SiteAction.Tunnel).Select(r => (JsonNode?)JsonValue.Create(r.Domain)).ToArray();
        if (tunnel.Length > 0)
            ((JsonArray)config["dns"]!["rules"]!).Insert(0, new JsonObject { ["domain_suffix"] = new JsonArray(tunnel), ["server"] = "remote-dns" });
    }

    // Правила пользователя. Порядок: DNS перехвачен выше; затем распознавание домена по
    // SNI/Host/QUIC, без него правила сайтов видели бы только IP; затем правила программ —
    // они относятся ко всей программе; затем правила сайтов, длинные домены раньше.
    // «Напрямую» — отдельный выход direct из этого ядра; защита WFP разрешает его только
    // при наличии таких правил (WfpProtectionBackend).
    // Выходы прямо на серверы и переключатель между ними. Ядро окна (local-core) остаётся в
    // переключателе: на узел без перевода (XHTTP и т. п.) окно переключает туда же, без перезапуска
    // туннеля и без снятия защиты (CoreSelector). Начальный выход — первый узел аренды.
    private static void AddServers(JsonObject config, ValidatedNetworkLease lease)
    {
        if (lease.Direct is not { } direct) return;
        var outbounds = (JsonArray)config["outbounds"]!;
        var members = new JsonArray();
        for (var i = 0; i < direct.Count; i++)
        {
            if (direct[i] is not { } outbound) continue;
            var copy = (JsonObject)outbound.DeepClone();
            copy["tag"] = DirectTag(i);
            outbounds.Add(copy); members.Add(DirectTag(i));
        }
        members.Add("local-core");
        outbounds.Add(new JsonObject
        {
            ["type"] = "selector", ["tag"] = ServerSelector, ["outbounds"] = members,
            ["default"] = direct[0] is null ? "local-core" : DirectTag(0), ["interrupt_exist_connections"] = true
        });
    }

    // Списки маршрутизации — после правил пользователя: явное правило сайта или программы сильнее
    // списка. Порядок наборов — как задал сервис. «Напрямую» без разрешения защиты (аренда без прямого
    // выхода) — блок: мимо VPN без разрешения трафик не уходит.
    private static void AddLists(JsonObject config, ValidatedNetworkLease lease, string? folder, string tunnel)
    {
        if (lease.Lists is not { } lists) return;
        if (folder is null) throw new UserError("Нет папки списков маршрутизации.");
        var sets = config["route"]!["rule_set"] as JsonArray ?? new JsonArray();
        config["route"]!["rule_set"] = sets;
        var rules = (JsonArray)config["route"]!["rules"]!;
        foreach (var set in lists.Order)
        {
            sets.Add(new JsonObject { ["type"] = "local", ["tag"] = RoutingLists.Tag(set), ["format"] = "source", ["path"] = RoutingLists.PathOf(folder, set) });
            var match = new JsonObject { ["rule_set"] = new JsonArray(RoutingLists.Tag(set)) };
            if (set == "block" || set == "direct" && !lease.HasDirect) match["action"] = "reject";
            else match["outbound"] = set == "direct" ? UserDirect : tunnel;
            rules.Add(match);
        }
        if (!lists.GlobalProxy && lease.HasDirect && !lease.DirectByDefault) config["route"]!["final"] = UserDirect;
    }

    public const string ServerSelector = "server";
    public static string DirectTag(int index) => "direct-" + index;

    private static void AddRules(JsonObject config, ValidatedNetworkLease lease, string? programSets, string tunnel)
    {
        var rules = (JsonArray)config["route"]!["rules"]!;
        if (lease.DirectExit) ((JsonArray)config["outbounds"]!).Add(new JsonObject { ["type"] = "direct", ["tag"] = UserDirect });
        // Соединения для «Сейчас в сети»: только 127.0.0.1 и только с секретом этой сессии.
        if (lease.Stats is { } stats)
            config["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = "127.0.0.1:" + stats.Port, ["secret"] = stats.Secret } };
        // Прямой трафик ядра окна (списки маршрутизации) приходит на свой локальный вход с паролем
        // сессии и уходит мимо туннеля. Правило — самое первое, раньше перехвата DNS: этот трафик
        // ядро уже разобрало, и возвращать его ядру нельзя — получилась бы петля.
        if (lease.CoreDirect is { } entry)
        {
            ((JsonArray)config["inbounds"]!).Add(new JsonObject
            {
                ["type"] = "socks", ["tag"] = CoreDirectInbound, ["listen"] = "127.0.0.1", ["listen_port"] = entry.Port,
                ["users"] = new JsonArray(new JsonObject { ["username"] = entry.User, ["password"] = entry.Password })
            });
            rules.Insert(0, new JsonObject { ["inbound"] = new JsonArray(CoreDirectInbound), ["outbound"] = UserDirect });
        }
        // Локальная сеть — раньше правил сайтов и программ: принтер и NAS не уходят на сервер.
        if (lease.LocalNetwork) rules.Add(new JsonObject { ["ip_cidr"] = new JsonArray(LocalSubnets.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["outbound"] = UserDirect });
        // Имя сайта ядро узнаёт из TLS/QUIC. С файлами правил сайт может появиться в правилах на ходу,
        // поэтому имя нужно всегда, а не только когда правила сайтов были при подключении.
        if (lease.SiteRules.Count > 0 || lease.Lists is not null || programSets is not null) rules.Add(new JsonObject { ["action"] = "sniff" });
        // Прямой выход разрешён защитой, только если «напрямую» было при подключении. Правило
        // «напрямую», добавленное на ходу без него, блокирует программу: мимо VPN без разрешения
        // она не уйдёт, а окно попросит переподключиться.
        // Правила с сайтом для одной программы — в тех же файлах, что правила программ (раньше них);
        // правила сайтов для всех программ — после правил программ, своими файлами (SiteRuleSets).
        if (programSets is not null)
        {
            JsonObject Guarded(JsonObject match, SiteAction action) => Route(match, action == SiteAction.Direct && !lease.HasDirect ? SiteAction.Block : action, tunnel);
            ProgramRuleSets.AddTo(config, programSets, Guarded);
            SiteRuleSets.AddTo(config, SiteRuleSets.NextTo(programSets), Guarded);
            return;
        }
        foreach (var rule in ProgramRoutingPlan.Ordered(lease.ProgramRules))
            rules.Add(Route(ProgramRoutingPlan.Match(rule), rule.Action, tunnel));
        foreach (var rule in lease.SiteRules.OrderByDescending(r => r.Domain.Count(c => c == '.')))
            rules.Add(Route(new JsonObject { ["domain_suffix"] = new JsonArray(rule.Domain) }, rule.Action, tunnel));
    }

    public const string UserDirect = "user-direct";
    // Частные сети без адресов самого туннеля (172.31.255.0/30 внутри 172.16/12 — правило DNS
    // стоит раньше, а другого трафика на адрес туннеля нет) и без CGNAT 100.64/10: там бывают
    // адреса провайдера, а не домашняя сеть.
    public static readonly string[] LocalSubnets = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"];
    public const string CoreDirectInbound = "core-direct";

    private static JsonObject Route(JsonObject match, SiteAction action, string tunnel)
    {
        if (action == SiteAction.Block) match["action"] = "reject";
        else match["outbound"] = action == SiteAction.Direct ? UserDirect : tunnel;
        return match;
    }
}

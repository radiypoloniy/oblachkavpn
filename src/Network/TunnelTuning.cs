using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

// Настройки туннеля из «Для продвинутых» (AdvancedOptions), которые уходят в службу: стек и MTU,
// свой DNS через VPN, подсети мимо туннеля. Служба проверяет их заново — окно могло прислать что
// угодно, а ядро туннеля работает с правами системы. null у поля — как без настройки.
public sealed record TunnelTuning(TunnelStack Stack = TunnelStack.Auto, int? Mtu = null, string? Dns = null, string[]? Bypass = null, CoreLogLevel Log = CoreLogLevel.Off,
    string? DirectDns = null, bool NoIpv6 = false, int? UdpTimeout = null, int? LanPort = null, string? LanPassword = null)
{
    public const string LanInbound = "lan";

    // provider — заголовки подписки сервиса: стек — только когда своего стека нет, подсети
    // складываются со своими. «Совместимость с играми» сильнее стека сервиса (compatible).
    public static TunnelTuning? From(AdvancedOptions options, ProviderOptions? provider = null, bool compatible = false)
    {
        var stack = options.Stack == TunnelStack.Auto && !compatible && provider?.Stack is { } offered ? offered : options.Stack;
        var bypass = options.BypassSubnets.Concat(provider?.ExcludeRoutes ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(AdvancedOptions.MaxSubnets).ToArray();
        return stack == TunnelStack.Auto && options.Mtu is null && options.TunnelDns is null && bypass.Length == 0 && options.CoreLog == CoreLogLevel.Off &&
            options.DirectDns is null && !options.NoIpv6 && options.UdpTimeout is null && !options.LanProxy ? null
            : new(stack, options.Mtu, options.TunnelDns, bypass.Length == 0 ? null : bypass, options.CoreLog,
                options.DirectDns, options.NoIpv6, options.UdpTimeout, options.LanProxy ? options.LanListenPort : null, options.LanProxy ? options.LanPassword : null);
    }

    // DNS для прямых сайтов: свой адрес или резолвер физической сети (DirectDns).
    public IPAddress? DirectResolver(Func<IPAddress?> physical) => DirectDns is { } own ? IPAddress.Parse(own) : physical();

    // Журнал ядра — в общей папке службы, где и журнал сессии: окно открывает его без прав
    // администратора. Путь задаёт служба, не окно: писать с правами системы туда, куда прислали, нельзя.
    public static string LogPath => Path.Combine(NetworkService.DataRoot, "core.log");

    public IReadOnlyList<string> Subnets => Bypass ?? [];

    public void Validate()
    {
        if (!Enum.IsDefined(Stack) || !Enum.IsDefined(Log)) throw new UserError("Неизвестный стек туннеля или уровень журнала.");
        if ((AdvancedOptions.CheckMtu(Mtu) ?? AdvancedOptions.CheckDns(Dns) ?? AdvancedOptions.CheckSubnets(Subnets) ?? AdvancedOptions.CheckDirectDns(DirectDns) ??
            AdvancedOptions.CheckUdpTimeout(UdpTimeout) ?? AdvancedOptions.CheckPort(LanPort) ?? AdvancedOptions.CheckLanPassword(LanPassword) ??
            (LanPort is null != LanPassword is null ? "Прокси для локальной сети — нужны и порт, и пароль." : null)) is { } problem)
            throw new UserError(problem);
    }

    // Правит готовую конфигурацию ядра туннеля. tunnel — выход «через VPN»: свой DNS ходит через него.
    public static void Apply(JsonObject config, TunnelTuning? tuning, string tunnel)
    {
        if (tuning is null) return;
        var tun = (JsonObject)((JsonArray)config["inbounds"]!)[0]!;
        if (tuning.Stack != TunnelStack.Auto) tun["stack"] = tuning.Stack.ToString().ToLowerInvariant();
        if (tuning.Mtu is { } mtu) tun["mtu"] = mtu;
        if (tuning.UdpTimeout is { } minutes) tun["udp_timeout"] = minutes + "m";
        // Подсети мимо туннеля — маршрутами Windows, как локальная сеть: трафик к ним ядро не видит.
        // Защита открывает их программам отдельным разрешением (WfpProtectionBackend).
        if (tuning.Subnets.Count > 0)
        {
            var exclusions = (JsonArray)tun["route_exclude_address"]!;
            foreach (var subnet in tuning.Subnets) exclusions.Add(Normalize(subnet));
        }
        if (tuning.Dns is { } dns) ReplaceDns(config, dns, tunnel);
        if (tuning.LanPort is { } lanPort) AddLan(config, lanPort, tuning.LanPassword!);
        if (tuning.Log != CoreLogLevel.Off)
            config["log"] = new JsonObject { ["level"] = tuning.Log == CoreLogLevel.Debug ? "debug" : "warn", ["output"] = LogPath, ["timestamp"] = true };
    }

    // Прокси для устройств локальной сети: SOCKS5 и HTTP на всех адресах IPv4, только с паролем.
    // Сам компьютер через него недоступен: имя сначала разрешается («localhost», «127.0.0.1.nip.io»
    // ушли бы прямым выходом на петлю), затем адреса петли отклоняются. Порт открывает в
    // брандмауэре служба только для локальной подсети (LanFirewall).
    private static void AddLan(JsonObject config, int port, string password)
    {
        ((JsonArray)config["inbounds"]!).Add(new JsonObject
        {
            ["type"] = "mixed", ["tag"] = LanInbound, ["listen"] = "0.0.0.0", ["listen_port"] = port,
            ["users"] = new JsonArray(new JsonObject { ["username"] = AdvancedOptions.LanUser, ["password"] = password })
        });
        var rules = (JsonArray)config["route"]!["rules"]!;
        rules.Insert(0, new JsonObject { ["inbound"] = new JsonArray(LanInbound), ["action"] = "resolve" });
        rules.Insert(1, new JsonObject { ["inbound"] = new JsonArray(LanInbound), ["ip_cidr"] = new JsonArray("127.0.0.0/8", "0.0.0.0/8", "::1/128", "::/128"), ["action"] = "reject" });
    }

    // «10.20.1.5/16» → «10.20.0.0/16»: ядро принимает только адрес сети.
    public static string Normalize(string subnet)
    {
        if (!AdvancedOptions.TryParseSubnet(subnet, out var address, out var prefix)) throw new UserError("Подсеть не разобрана.");
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes) + "/" + prefix;
    }

    // Встроенный DoH (remote-dns) заменяется своим сервером с тем же тегом: правила DNS на него
    // уже ссылаются. Только DoH: DoT и DoQ через встроенный в ядро Xray (серверы XHTTP) отвечали
    // первые секунды и замолкали (стенд 25.10.2026), а DoH на 443 работал у любого сервера. Имя
    // сервера разрешает встроенный DoH 1.1.1.1 — он загрузочный: UDP через тот же Xray тоже ненадёжен.
    private static void ReplaceDns(JsonObject config, string dns, string tunnel)
    {
        var uri = new Uri(dns);
        var server = new JsonObject
        {
            ["type"] = "https", ["tag"] = "remote-dns", ["server"] = uri.Host, ["server_port"] = uri.IsDefaultPort ? 443 : uri.Port,
            ["path"] = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath : "/dns-query", ["detour"] = tunnel
        };
        var servers = (JsonArray)config["dns"]!["servers"]!;
        var index = servers.Select((s, i) => (s, i)).First(p => (string?)p.s!["tag"] == "remote-dns").i;
        if (!IPAddress.TryParse(uri.Host, out _))
        {
            var bootstrap = servers[index]!.DeepClone().AsObject();
            bootstrap["tag"] = "dns-bootstrap";
            servers.Add(bootstrap);
            server["domain_resolver"] = "dns-bootstrap";
            server["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = uri.Host };
        }
        servers[index] = server;
    }
}

using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

// Настройки туннеля из «Для продвинутых» (AdvancedOptions), которые уходят в службу: стек и MTU,
// свой DNS через VPN, подсети мимо туннеля. Служба проверяет их заново — окно могло прислать что
// угодно, а ядро туннеля работает с правами системы. null у поля — как без настройки.
public sealed record TunnelTuning(TunnelStack Stack = TunnelStack.Auto, int? Mtu = null, string? Dns = null, string[]? Bypass = null)
{
    public static TunnelTuning? From(AdvancedOptions options) =>
        options.Stack == TunnelStack.Auto && options.Mtu is null && options.TunnelDns is null && options.BypassSubnets.Length == 0 ? null
            : new(options.Stack, options.Mtu, options.TunnelDns, options.BypassSubnets.Length == 0 ? null : options.BypassSubnets);

    public IReadOnlyList<string> Subnets => Bypass ?? [];

    public void Validate()
    {
        if (!Enum.IsDefined(Stack)) throw new UserError("Неизвестный стек туннеля.");
        if ((AdvancedOptions.CheckMtu(Mtu) ?? AdvancedOptions.CheckDns(Dns) ?? AdvancedOptions.CheckSubnets(Subnets)) is { } problem)
            throw new UserError(problem);
    }

    // Правит готовую конфигурацию ядра туннеля. tunnel — выход «через VPN»: свой DNS ходит через него.
    public static void Apply(JsonObject config, TunnelTuning? tuning, string tunnel)
    {
        if (tuning is null) return;
        var tun = (JsonObject)((JsonArray)config["inbounds"]!)[0]!;
        if (tuning.Stack != TunnelStack.Auto) tun["stack"] = tuning.Stack.ToString().ToLowerInvariant();
        if (tuning.Mtu is { } mtu) tun["mtu"] = mtu;
        // Подсети мимо туннеля — маршрутами Windows, как локальная сеть: трафик к ним ядро не видит.
        // Защита открывает их программам отдельным разрешением (WfpProtectionBackend).
        if (tuning.Subnets.Count > 0)
        {
            var exclusions = (JsonArray)tun["route_exclude_address"]!;
            foreach (var subnet in tuning.Subnets) exclusions.Add(Normalize(subnet));
        }
        if (tuning.Dns is { } dns) ReplaceDns(config, dns, tunnel);
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
    // уже ссылаются. Имя сервера разрешает загрузочный DNS 1.1.1.1 — тоже через туннель.
    private static void ReplaceDns(JsonObject config, string dns, string tunnel)
    {
        var uri = new Uri(dns);
        var server = new JsonObject { ["tag"] = "remote-dns", ["server"] = uri.Host, ["detour"] = tunnel };
        switch (uri.Scheme)
        {
            case "https":
                server["type"] = "https"; server["server_port"] = uri.IsDefaultPort ? 443 : uri.Port;
                server["path"] = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath : "/dns-query";
                break;
            default:
                server["type"] = uri.Scheme; server["server_port"] = uri.IsDefaultPort || uri.Port < 0 ? 853 : uri.Port;
                break;
        }
        var servers = (JsonArray)config["dns"]!["servers"]!;
        if (!IPAddress.TryParse(uri.Host, out _))
        {
            servers.Add(new JsonObject { ["type"] = "udp", ["tag"] = "dns-bootstrap", ["server"] = "1.1.1.1", ["detour"] = tunnel });
            server["domain_resolver"] = "dns-bootstrap";
        }
        var index = servers.Select((s, i) => (s, i)).First(p => (string?)p.s!["tag"] == "remote-dns").i;
        servers[index] = server;
    }
}

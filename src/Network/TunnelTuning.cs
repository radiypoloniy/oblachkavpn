using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

// Настройки туннеля из «Для продвинутых» (AdvancedOptions), которые уходят в службу: стек и MTU,
// свой DNS через VPN, подсети мимо туннеля. Служба проверяет их заново — окно могло прислать что
// угодно, а ядро туннеля работает с правами системы. null у поля — как без настройки.
public sealed record TunnelTuning(TunnelStack Stack = TunnelStack.Auto, int? Mtu = null, string? Dns = null, string[]? Bypass = null, CoreLogLevel Log = CoreLogLevel.Off)
{
    public static TunnelTuning? From(AdvancedOptions options) =>
        options.Stack == TunnelStack.Auto && options.Mtu is null && options.TunnelDns is null && options.BypassSubnets.Length == 0 && options.CoreLog == CoreLogLevel.Off ? null
            : new(options.Stack, options.Mtu, options.TunnelDns, options.BypassSubnets.Length == 0 ? null : options.BypassSubnets, options.CoreLog);

    // Журнал ядра — в общей папке службы, где и журнал сессии: окно открывает его без прав
    // администратора. Путь задаёт служба, не окно: писать с правами системы туда, куда прислали, нельзя.
    public static string LogPath => Path.Combine(NetworkService.DataRoot, "core.log");

    public IReadOnlyList<string> Subnets => Bypass ?? [];

    public void Validate()
    {
        if (!Enum.IsDefined(Stack) || !Enum.IsDefined(Log)) throw new UserError("Неизвестный стек туннеля или уровень журнала.");
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
        if (tuning.Log != CoreLogLevel.Off)
            config["log"] = new JsonObject { ["level"] = tuning.Log == CoreLogLevel.Debug ? "debug" : "warn", ["output"] = LogPath, ["timestamp"] = true };
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

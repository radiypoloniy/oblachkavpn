using System.Net;

namespace Oblachka;

// «Проверить сеть Windows»: поиск поломок, которые оставляют упавшие VPN-клиенты и обходы
// блокировок (Throne, v2rayN, Clash, zapret, GoodbyeDPI). Только чтение: исправление — отдельно
// и только найденного (NetworkRepair), с возможностью вернуть как было.
//
// Чего не трогаем никогда: статический IP, свой DNS человека (если он отвечает), работающий
// чужой VPN, правила брандмауэра, драйверы. Полный сброс — встроенный «Сброс сети» Windows.
public enum RepairKind
{
    DeadProxy, DeadPac, DeadEnvProxy, FlushDns,          // без прав администратора
    WinHttpProxy, AdapterDns, DeadRoute, StopBypass,     // через службу Облачков
    None                                                 // только сведения
}

public sealed record RepairAction(RepairKind Kind, string? Target = null)
{
    public bool Elevated => Kind is RepairKind.WinHttpProxy or RepairKind.AdapterDns or RepairKind.DeadRoute or RepairKind.StopBypass;
}

public sealed record NetworkFinding(string Title, string Text, RepairAction Action);

public sealed record AdapterInfo(string Id, string Name, bool StaticDns, IPAddress[] Dns);
public sealed record RouteInfo(string Destination, string Mask, string NextHop, int InterfaceIndex, bool InterfaceUp);
public sealed record BypassInfo(string Name, bool IsService);

public interface INetworkProbe
{
    ProxySettings Proxy();
    bool Listening(int port);
    IReadOnlyDictionary<string, string> EnvironmentProxies();
    string? WinHttpProxy();
    IReadOnlyList<AdapterInfo> Adapters();
    bool AnswersDns(IPAddress server);
    IReadOnlyList<RouteInfo> DefaultRoutes();
    IReadOnlyList<BypassInfo> BypassTools();
    IReadOnlyList<string> HostsEntries();
    string? ForeignVpn();
}

public static class NetworkDoctor
{
    // Порт на этом компьютере из «127.0.0.1:2080», «http=127.0.0.1:2080;https=…», «socks5://localhost:1080».
    internal static IEnumerable<int> LoopbackPorts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var part in value.Split(';', ' ', ','))
        {
            var text = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
            if (text.Contains("://")) text = text[(text.IndexOf("://") + 3)..];
            text = text.TrimEnd('/');
            var slash = text.IndexOf('/'); if (slash >= 0) text = text[..slash];
            var colon = text.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(text[(colon + 1)..], out var port) || port is < 1 or > 65535) continue;
            var host = text[..colon].Trim('[', ']').ToLowerInvariant();
            if (host is "localhost" or "::1" || host.StartsWith("127.")) yield return port;
        }
    }

    // Прокси указывает на этот компьютер, а там никто не отвечает — значит, его оставил упавший клиент.
    private static int? DeadPort(INetworkProbe probe, string? value) =>
        LoopbackPorts(value).Cast<int?>().FirstOrDefault(p => !probe.Listening(p!.Value));

    internal static bool FakeDns(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes();
        // 198.18.0.0/15 — «поддельные» адреса Clash и sing-box (fake-ip).
        return b.Length == 4 && b[0] == 198 && (b[1] == 18 || b[1] == 19);
    }

    public static IReadOnlyList<NetworkFinding> Diagnose(INetworkProbe probe)
    {
        var found = new List<NetworkFinding>();
        var foreign = probe.ForeignVpn();
        if (foreign is not null)
            found.Add(new("Работает " + foreign, "Пока он включён, его настройки не трогаем: выключите его сами, если интернет не работает.", new(RepairKind.None)));

        var proxy = probe.Proxy();
        if (proxy.ProxyEnable == 1 && foreign is null && DeadPort(probe, proxy.ProxyServer) is int port)
            found.Add(new("Системный прокси ведёт в никуда",
                $"Прокси Windows указывает на этот компьютер (порт {port}), но там никто не отвечает — так бывает, когда Throne, v2rayN или Clash закрылись аварийно. Браузеры из-за этого не открывают сайты.",
                new(RepairKind.DeadProxy)));
        if (foreign is null && DeadPort(probe, proxy.AutoConfigUrl) is int pacPort)
            found.Add(new("Автонастройка прокси ведёт в никуда",
                $"Windows берёт настройки прокси с этого компьютера (порт {pacPort}), но там никто не отвечает.", new(RepairKind.DeadPac)));

        foreach (var (name, value) in probe.EnvironmentProxies())
            if (DeadPort(probe, value) is int envPort)
                found.Add(new("Переменная " + name + " ведёт в никуда",
                    $"Программы для разработчиков и часть утилит идут через прокси на порт {envPort}, а там никто не отвечает.", new(RepairKind.DeadEnvProxy, name)));

        var winHttp = probe.WinHttpProxy();
        if (winHttp is not null && DeadPort(probe, winHttp) is int winHttpPort)
            found.Add(new("Прокси служб Windows ведёт в никуда",
                $"Обновления Windows и службы идут через прокси на порт {winHttpPort}, а там никто не отвечает.", new(RepairKind.WinHttpProxy)));

        foreach (var adapter in probe.Adapters())
        {
            var fake = adapter.Dns.Where(FakeDns).ToArray();
            // Свой локальный DNS (AdGuard Home, dnscrypt) отвечает — его не трогаем.
            if (adapter.StaticDns && fake.Length > 0 && !fake.Any(probe.AnswersDns))
                found.Add(new("DNS сети «" + adapter.Name + "» не отвечает",
                    $"У подключения вручную записан DNS {string.Join(", ", fake.Select(a => a.ToString()))} — его оставил VPN-клиент или фильтр. Сайты не находятся по имени.",
                    new(RepairKind.AdapterDns, adapter.Id)));
        }

        foreach (var route in probe.DefaultRoutes().Where(r => !r.InterfaceUp))
            found.Add(new("Маршрут в отключённое подключение",
                $"Трафик {route.Destination}/{route.Mask} уходит в подключение, которого уже нет (оставил VPN-клиент).",
                new(RepairKind.DeadRoute, $"{route.Destination}|{route.Mask}|{route.NextHop}|{route.InterfaceIndex}")));

        foreach (var tool in probe.BypassTools())
            found.Add(new("Работает " + tool.Name,
                "Обход блокировок меняет пакеты всех программ. С неподходящими настройками он ломает сайты и VPN, а запущенным его легко забыть" +
                (tool.IsService ? " — он установлен службой и стартует вместе с Windows." : "."), new(RepairKind.StopBypass, tool.Name)));

        var hosts = probe.HostsEntries();
        if (hosts.Count > 0)
            found.Add(new("В файле hosts есть записи: " + hosts.Count,
                "Их добавляют инструкции по обходу блокировок; устаревшие записи ведут сайты на неработающие адреса. Сами записи не трогаем: " +
                string.Join(", ", hosts.Take(3)) + (hosts.Count > 3 ? "…" : ""), new(RepairKind.None)));

        if (found.Any(f => f.Action.Kind != RepairKind.None)) found.Add(new("Кэш DNS", "Очистим после исправлений, чтобы старые ответы не мешали.", new(RepairKind.FlushDns)));
        return found;
    }
}

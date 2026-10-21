using System.Net;

namespace Oblachka;

// «Соединения»: программы, которые сейчас в сети, и куда идёт их трафик. Одна запись — один файл
// программы (по пути): у Chrome и svchost процессов много, а правило одно. Окно склеивает файлы
// одной программы (Steam, steamwebhelper, игра) в строку и раскрывает их по нажатию.
// Speed — байт в секунду (только по данным ядра туннеля), Sites — с кем программа на связи.
public sealed record LiveApp(string Path, int Vpn, int Direct, int Udp, string[] Remotes)
{
    public int Total => Vpn + Direct;
    public long Speed { get; init; }
    public IReadOnlyList<LiveSite> Sites { get; init; } = [];
}

// Сайт (или адрес без имени) внутри программы: сколько соединений куда идёт. Ids — соединения ядра
// для «Закрыть»; по таблицам Windows их нет.
public sealed record LiveSite(string Name, int Vpn, int Direct, long Speed, string[] Ids)
{
    public int Total => Vpn + Direct;
}

public static class LiveApps
{
    // tunnel — «Весь компьютер»: через VPN то, что открыто с адреса туннеля. Иначе через VPN —
    // только соединения с прокси Облачки на proxyPort (браузеры и программы, что его слушают).
    public static IReadOnlyList<LiveApp> Group(IReadOnlyList<LiveSocket> sockets, bool tunnel, int proxyPort, Func<int, string?> pathOf, Func<string, bool> hidden)
    {
        var tunnelHosts = new[] { IPAddress.Parse(SingBoxTunConfig.TunHost4), IPAddress.Parse(SingBoxTunConfig.TunHost6) };
        var paths = new Dictionary<int, string?>();
        var groups = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        foreach (var socket in sockets)
        {
            if (!paths.TryGetValue(socket.Pid, out var path)) paths[socket.Pid] = path = pathOf(socket.Pid);
            if (path is null || hidden(path)) continue;
            if (!groups.TryGetValue(path, out var group)) groups[path] = group = new Tally();
            if (socket.Udp) group.Udp++;
            else if (socket.Remote is { } remote && Counts(socket))
            {
                var viaProxy = IPAddress.IsLoopback(remote) && socket.RemotePort == proxyPort;
                if (IPAddress.IsLoopback(remote) && !viaProxy) continue;
                var vpn = tunnel ? tunnelHosts.Contains(Plain(socket.Local)) : viaProxy;
                group.Add(viaProxy ? "через прокси Облачки" : Plain(remote) + ":" + socket.RemotePort, vpn, 0, null);
            }
        }
        return Apps(groups.Where(g => g.Value.Vpn + g.Value.Direct > 0));
    }

    // «Весь компьютер» по данным ядра туннеля (CoreConnections): напрямую — то, что ушло в его
    // прямой выход по правилу программы или сайта, и то, что ядро окна по своим спискам
    // («Российские сайты») вернуло туннелю на прямой вход: такое соединение программы ядро
    // туннеля видит идущим в VPN, а прямое соединение к тому же адресу открывает ядро окна.
    // speed — байт в секунду по соединению (LiveSpeed); без него скорость нулевая.
    public static IReadOnlyList<LiveApp> FromCore(IReadOnlyList<CoreConnection> connections, Func<string, bool> hidden, IReadOnlyDictionary<string, long>? speed = null)
    {
        static IEnumerable<string> Keys(CoreConnection c) =>
            new[] { c.Address, c.Host }.OfType<string>().Select(a => a + ":" + c.Port);
        var listed = new HashSet<string>(connections.Where(c => c.ProcessPath is { } p && hidden(p) && c.Direct).SelectMany(Keys), StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in connections)
        {
            if (c.ProcessPath is not { } path || hidden(path)) continue;
            if (!groups.TryGetValue(path, out var group)) groups[path] = group = new Tally();
            if (c.Udp) group.Udp++;
            var bytes = c.Id is { } id && speed?.GetValueOrDefault(id) is { } s ? s : 0;
            group.Add(c.Host ?? c.Address ?? "?", !(c.Direct || Keys(c).Any(listed.Contains)), bytes, c.Id, c.Port);
        }
        return Apps(groups);
    }

    private static IReadOnlyList<LiveApp> Apps(IEnumerable<KeyValuePair<string, Tally>> groups) => groups
        .Select(g => new LiveApp(g.Key, g.Value.Vpn, g.Value.Direct, g.Value.Udp, g.Value.Remotes.ToArray())
        {
            Speed = g.Value.Speed,
            Sites = g.Value.Sites.Select(s => new LiveSite(s.Key, s.Value.Vpn, s.Value.Direct, s.Value.Speed, s.Value.Ids.ToArray()))
                .OrderByDescending(s => s.Speed).ThenByDescending(s => s.Total).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray()
        })
        .OrderByDescending(a => a.Total).ThenBy(a => System.IO.Path.GetFileName(a.Path), StringComparer.OrdinalIgnoreCase).ToArray();

    // Только живые соединения: установленные и устанавливаемые, без ожидания входящих.
    private static bool Counts(LiveSocket socket) => socket.State is >= 3 and <= 5 && socket.Remote is { } r && !r.Equals(IPAddress.Any) && !r.Equals(IPAddress.IPv6Any);

    private sealed class Tally
    {
        public int Vpn, Direct, Udp;
        public long Speed;
        public readonly List<string> Remotes = new();
        public readonly Dictionary<string, SiteTally> Sites = new(StringComparer.OrdinalIgnoreCase);

        // port — для строки «с кем на связи»; сайт в списке сайтов — без порта.
        public void Add(string site, bool vpn, long speed, string? id, int port = 0)
        {
            if (vpn) Vpn++; else Direct++;
            Speed += speed;
            var endpoint = port > 0 ? site + ":" + port : site;
            if (Remotes.Count < 3 && !Remotes.Contains(endpoint)) Remotes.Add(endpoint);
            if (!Sites.TryGetValue(site, out var tally)) Sites[site] = tally = new SiteTally();
            if (vpn) tally.Vpn++; else tally.Direct++;
            tally.Speed += speed;
            if (id is not null) tally.Ids.Add(id);
        }
    }

    private sealed class SiteTally { public int Vpn, Direct; public long Speed; public readonly List<string> Ids = new(); }

    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

// Скорость соединений ядра: разница байтов между двумя опросами. Новое соединение в первый раз
// считается с нуля — иначе всё, что оно передало до опроса, выглядело бы мгновенным всплеском.
public sealed class LiveSpeed
{
    private Dictionary<string, long> last = new();
    private DateTime at;

    public IReadOnlyDictionary<string, long> Measure(IReadOnlyList<CoreConnection> connections, DateTime now)
    {
        var seconds = (now - at).TotalSeconds;
        var next = new Dictionary<string, long>();
        var speed = new Dictionary<string, long>();
        foreach (var c in connections)
        {
            if (c.Id is not { } id) continue;
            var bytes = c.Upload + c.Download;
            next[id] = bytes;
            if (seconds is > 0.2 and < 30 && last.TryGetValue(id, out var before) && bytes >= before) speed[id] = (long)((bytes - before) / seconds);
        }
        last = next; at = now;
        return speed;
    }
}

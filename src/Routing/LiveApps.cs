using System.Net;

namespace Oblachka;

// «Соединения»: программы, которые сейчас в сети, и куда идёт их трафик. Одна строка — одна
// программа (по пути файла): у Chrome и svchost процессов много, а правило одно.
public sealed record LiveApp(string Path, int Vpn, int Direct, int Udp, string[] Remotes)
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
                if (tunnel ? tunnelHosts.Contains(Plain(socket.Local)) : viaProxy) group.Vpn++; else group.Direct++;
                var endpoint = viaProxy ? "через прокси Облачки" : Plain(remote) + ":" + socket.RemotePort;
                if (group.Remotes.Count < 3 && !group.Remotes.Contains(endpoint)) group.Remotes.Add(endpoint);
            }
        }
        return groups.Where(g => g.Value.Vpn + g.Value.Direct > 0)
            .Select(g => new LiveApp(g.Key, g.Value.Vpn, g.Value.Direct, g.Value.Udp, g.Value.Remotes.ToArray()))
            .OrderByDescending(a => a.Total).ThenBy(a => System.IO.Path.GetFileName(a.Path), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // «Весь компьютер» по данным ядра туннеля (CoreConnections): напрямую — то, что ушло в его
    // прямой выход по правилу программы или сайта, и то, что ядро окна по своим спискам
    // («Российские сайты») вернуло туннелю на прямой вход: такое соединение программы ядро
    // туннеля видит идущим в VPN, а прямое соединение к тому же адресу открывает ядро окна.
    public static IReadOnlyList<LiveApp> FromCore(IReadOnlyList<CoreConnection> connections, Func<string, bool> hidden)
    {
        static bool Direct(CoreConnection c) => c.Chains.Contains(SingBoxTunConfig.UserDirect);
        static IEnumerable<string> Keys(CoreConnection c) =>
            new[] { c.Address, c.Host }.OfType<string>().Select(a => a + ":" + c.Port);
        var listed = new HashSet<string>(connections.Where(c => c.ProcessPath is { } p && hidden(p) && Direct(c)).SelectMany(Keys), StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in connections)
        {
            if (c.ProcessPath is not { } path || hidden(path)) continue;
            if (!groups.TryGetValue(path, out var group)) groups[path] = group = new Tally();
            if (c.Udp) group.Udp++;
            if (Direct(c) || Keys(c).Any(listed.Contains)) group.Direct++; else group.Vpn++;
            var endpoint = (c.Host ?? c.Address) + ":" + c.Port;
            if (group.Remotes.Count < 3 && !group.Remotes.Contains(endpoint)) group.Remotes.Add(endpoint);
        }
        return groups.Select(g => new LiveApp(g.Key, g.Value.Vpn, g.Value.Direct, g.Value.Udp, g.Value.Remotes.ToArray()))
            .OrderByDescending(a => a.Total).ThenBy(a => System.IO.Path.GetFileName(a.Path), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Только живые соединения: установленные и устанавливаемые, без ожидания входящих.
    private static bool Counts(LiveSocket socket) => socket.State is >= 3 and <= 5 && socket.Remote is { } r && !r.Equals(IPAddress.Any) && !r.Equals(IPAddress.IPv6Any);

    private sealed class Tally { public int Vpn, Direct, Udp; public readonly List<string> Remotes = new(); }

    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

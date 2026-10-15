using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Oblachka;

// Настоящие данные Windows для NetworkDoctor. Только чтение, без прав администратора.
public sealed class WindowsNetworkProbe : INetworkProbe
{
    public static readonly string[] BypassServices = { "zapret", "winws1", "winws2", "GoodbyeDPI" };
    public static readonly string[] BypassProcesses = { "winws", "goodbyedpi" };

    public ProxySettings Proxy() => new WinInetProxyStore().Read();

    public bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(400) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException) { return false; }
    }

    public IReadOnlyDictionary<string, string> EnvironmentProxies() =>
        new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }
            .Select(n => (n, v: Environment.GetEnvironmentVariable(n, EnvironmentVariableTarget.User)))
            .Where(x => !string.IsNullOrWhiteSpace(x.v)).ToDictionary(x => x.n, x => x.v!);

    public string? WinHttpProxy()
    {
        var info = new ProxyInfo();
        if (!WinHttpGetDefaultProxyConfiguration(ref info)) return null;
        try { return info.AccessType == 3 && info.Proxy != IntPtr.Zero ? Marshal.PtrToStringUni(info.Proxy) : null; }
        finally { if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy); if (info.Bypass != IntPtr.Zero) GlobalFree(info.Bypass); }
    }

    public IReadOnlyList<AdapterInfo> Adapters()
    {
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                nic.Name.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.Ordinal)) continue;
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + nic.Id);
            var manual = key?.GetValue("NameServer") is string servers && servers.Trim().Length > 0;
            var dns = nic.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            result.Add(new(nic.Id, nic.Name, manual, dns));
        }
        return result;
    }

    // Отвечает ли DNS-сервер: запрос A для www.microsoft.com, ждём ответ с тем же номером.
    public bool AnswersDns(IPAddress server)
    {
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            var id = (ushort)Random.Shared.Next(1, 65535);
            var query = new List<byte> { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in "www.microsoft.com".Split('.')) { query.Add((byte)label.Length); query.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
            query.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            udp.Send(query.ToArray(), query.Count, new IPEndPoint(server, 53));
            var receive = udp.ReceiveAsync();
            return receive.Wait(800) && receive.Result.Buffer.Length >= 12 && receive.Result.Buffer[0] == (byte)(id >> 8) && receive.Result.Buffer[1] == (byte)id;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or ObjectDisposedException) { return false; }
    }

    public IReadOnlyList<RouteInfo> DefaultRoutes()
    {
        var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Select(n => { try { return n.GetIPProperties().GetIPv4Properties()?.Index ?? -1; } catch (NetworkInformationException) { return -1; } }).ToHashSet();
        var result = new List<RouteInfo>();
        foreach (var row in RouteTable())
        {
            var (dest, mask) = (new IPAddress(row.Dest), new IPAddress(row.Mask));
            var text = dest + "/" + mask;
            if (text is not ("0.0.0.0/0.0.0.0" or "0.0.0.0/128.0.0.0" or "128.0.0.0/128.0.0.0")) continue;
            result.Add(new(dest.ToString(), mask.ToString(), new IPAddress(row.NextHop).ToString(), (int)row.IfIndex, up.Contains((int)row.IfIndex)));
        }
        return result;
    }

    public IReadOnlyList<BypassInfo> BypassTools()
    {
        var result = new List<BypassInfo>();
        foreach (var name in BypassProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length == 0) continue;
                var asService = processes.Any(p => p.SessionId == 0);
                var label = name == "winws" ? "zapret (winws)" : "GoodbyeDPI";
                result.Add(new(label, asService || BypassServices.Any(s => Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + s) is { } k && Close(k))));
            }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        return result;
    }

    private static bool Close(RegistryKey key) { key.Dispose(); return true; }

    public IReadOnlyList<string> HostsEntries()
    {
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        try
        {
            // Только записи, уводящие сайт на внешний адрес: их добавляют инструкции по обходу.
            // Локальные (Docker, свои серверы) и блокирующие (0.0.0.0, 127.0.0.1) — не наша забота.
            return File.ReadLines(path).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0)
                .Where(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } p &&
                            IPAddress.TryParse(p[0], out var ip) && IsPublic(ip))
                .Take(200).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    internal static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
        var b = ip.GetAddressBytes();
        return b.Length != 4 || !(b[0] == 10 || b[0] == 0 || b[0] == 127 || b[0] == 172 && b[1] >= 16 && b[1] < 32 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] >= 64 && b[1] < 128);
    }

    public string? ForeignVpn() => ForeignTunnel.Find() is { } adapter ? ForeignTunnel.FriendlyName(adapter) : null;

    [StructLayout(LayoutKind.Sequential)] private struct ProxyInfo { public uint AccessType; public IntPtr Proxy; public IntPtr Bypass; }
    [DllImport("winhttp.dll", SetLastError = true)] private static extern bool WinHttpGetDefaultProxyConfiguration(ref ProxyInfo info);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct ForwardRow { public uint Dest, Mask, Policy, NextHop, IfIndex, Type, Proto, Age, NextHopAs, Metric1, Metric2, Metric3, Metric4, Metric5; }
    [DllImport("iphlpapi.dll")] private static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);

    private static IEnumerable<ForwardRow> RouteTable()
    {
        var size = 0;
        GetIpForwardTable(IntPtr.Zero, ref size, false);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpForwardTable(buffer, ref size, false) != 0) return Array.Empty<ForwardRow>();
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<ForwardRow>();
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStructure<ForwardRow>(buffer + 4 + i * rowSize)).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

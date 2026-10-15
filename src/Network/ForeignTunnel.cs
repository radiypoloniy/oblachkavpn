using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Oblachka;

// Другой VPN, который забрал весь трафик в свой туннель.
//
// Найдено проверкой на настоящей машине с Throne в режиме TUN: наш туннель поднимается
// и по адресу трафик идёт через него, но имена не разрешаются. У обоих туннелей строгая
// маршрутизация, и каждый закрывает DNS другого: Windows спрашивает DNS чужого адаптера —
// его режут наши фильтры, спрашивает наш — его режет чужой туннель. Ослабить свою защиту
// ради чужого DNS значит открыть обход, поэтому системный режим при таком VPN не
// запускается, а пользователь получает понятное объяснение.
//
// Признак — не список известных программ, а маршрут: через какой интерфейс Windows
// отправит пакет в интернет. Чужой VPN, который не забирает весь трафик (например,
// корпоративная сеть с отдельными маршрутами), не мешает и не считается.
public static class ForeignTunnel
{
    // Тип интерфейса wintun, WireGuard и большинства TUN-клиентов (IF_TYPE_PROP_VIRTUAL).
    private const int PropVirtual = 53;
    private static readonly IPAddress[] Probes = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("2606:4700:4700::1111") };

    // Имя адаптера другого VPN, или null, если интернет идёт мимо чужих туннелей.
    public static string? Find()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var probe in Probes)
            {
                if (!BestInterface(probe, out var index)) continue;
                var nic = interfaces.FirstOrDefault(n => Index(n, probe.AddressFamily) == index);
                if (nic is not null && IsForeign(nic.Name, (int)nic.NetworkInterfaceType, nic.Description)) return nic.Name;
            }
        }
        catch (NetworkInformationException) { }
        return null;
    }

    // Чужой туннель: виртуальный интерфейс TUN, TAP или PPP (встроенный VPN Windows), не наш.
    public static bool IsForeign(string name, int type, string description) =>
        !name.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.OrdinalIgnoreCase) &&
        (type == PropVirtual || type == (int)NetworkInterfaceType.Ppp ||
         description.Contains("TAP-", StringComparison.OrdinalIgnoreCase));

    // Название программы вместо имени адаптера: «Throne», а не «throne-tun».
    public static string FriendlyName(string adapter)
    {
        var name = adapter.ToLowerInvariant();
        (string Key, string Title)[] known = [("throne", "Throne"), ("nekobox", "NekoBox"), ("nekoray", "NekoRay"), ("hiddify", "Hiddify"),
            ("amnezia", "AmneziaVPN"), ("wireguard", "WireGuard"), ("wg", "WireGuard"), ("openvpn", "OpenVPN"), ("tap", "OpenVPN"),
            ("outline", "Outline"), ("clash", "Clash"), ("mihomo", "Clash"), ("v2ray", "v2rayN"), ("happ", "Happ"), ("proton", "Proton VPN")];
        foreach (var (key, title) in known) if (name.Contains(key, StringComparison.Ordinal)) return title;
        return "«" + adapter + "»";
    }

    public static string Explain(string adapter) =>
        "Включён другой VPN (адаптер «" + adapter + "»), и он забирает весь трафик в свой туннель. " +
        "Вместе с ним системный режим не работает: каждый VPN закрывает DNS другого, и сайты не открываются. " +
        "Выключите другой VPN или его режим TUN, либо подключитесь в обычном режиме.";

    private static int Index(NetworkInterface nic, AddressFamily family)
    {
        try
        {
            var properties = nic.GetIPProperties();
            return family == AddressFamily.InterNetwork
                ? properties.GetIPv4Properties()?.Index ?? -1
                : properties.GetIPv6Properties()?.Index ?? -1;
        }
        catch (NetworkInformationException) { return -1; }
    }

    private static bool BestInterface(IPAddress address, out int index)
    {
        // SOCKADDR_IN (16 байт) или SOCKADDR_IN6 (28 байт): семейство, порт 0, адрес.
        var v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var sockaddr = new byte[v6 ? 28 : 16];
        BitConverter.GetBytes((ushort)(v6 ? 23 : 2)).CopyTo(sockaddr, 0);
        address.GetAddressBytes().CopyTo(sockaddr, v6 ? 8 : 4);
        var result = GetBestInterfaceEx(sockaddr, out var best);
        index = (int)best;
        return result == 0;
    }

    [DllImport("iphlpapi.dll")] private static extern int GetBestInterfaceEx(byte[] destination, out uint index);
}

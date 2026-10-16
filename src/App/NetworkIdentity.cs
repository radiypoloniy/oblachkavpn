using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Oblachka;

// Какая это сеть — чтобы запоминать, что в ней работает (стратегия zapret и т. п.).
// Берётся физический адаптер с основным шлюзом: его идентификатор и адрес шлюза. Туннели
// (наш Obl-…, Wintun, TAP других VPN) не в счёт. В файл состояния попадает только хеш.
public static class NetworkIdentity
{
    public static string? Current() => Physical() is { } found
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(found.Adapter.Id + "|" + found.Gateway)))[..16]
        : null;

    // Физический адаптер с основным шлюзом; null — сети нет.
    public static (NetworkInterface Adapter, System.Net.IPAddress Gateway)? Physical()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1))
            {
                if (nic.Name.StartsWith("Obl-", StringComparison.OrdinalIgnoreCase) ||
                    new[] { "wintun", "tap-", "wireguard", "sing-box", "tun" }.Any(t => nic.Description.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                var gateway = nic.GetIPProperties().GatewayAddresses.Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any));
                if (gateway is null) continue;
                return (nic, gateway);
            }
        }
        catch (NetworkInformationException) { }
        return null;
    }
}

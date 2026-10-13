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

    public static JsonObject Create(NetworkLeaseRequest request)
    {
        var lease = NetworkHelperContracts.Validate(request);
        if (lease.Action != NetworkLeaseAction.Acquire) throw new UserError("TUN требует новую сетевую сессию.");
        var exclusions = new JsonArray(lease.Peers.Select(ip => (JsonNode?)JsonValue.Create(
            ip + (ip.AddressFamily == AddressFamily.InterNetwork ? "/32" : "/128"))).ToArray());
        return new JsonObject
        {
            ["log"] = new JsonObject { ["disabled"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject
                {
                    ["type"] = "https", ["tag"] = "remote-dns", ["server"] = "1.1.1.1", ["server_port"] = 443,
                    ["path"] = "/dns-query", ["detour"] = "local-core",
                    ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" }
                }),
                ["final"] = "remote-dns"
            },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "tun", ["tag"] = "system-tun", ["interface_name"] = "Obl-" + lease.LeaseId.ToString("N")[..8],
                ["address"] = new JsonArray(TunHost4 + "/30", TunHost6 + "/126"),
                ["mtu"] = 1500, ["stack"] = "mixed", ["auto_route"] = true, ["strict_route"] = true,
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
                ["auto_detect_interface"] = true, ["final"] = "local-core",
                // Порядок важен: DNS перехватывается первым правилом и уходит на DoH внутри
                // туннеля. Остальной UDP идёт в туннель наравне с TCP.
                ["rules"] = new JsonArray(
                    new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" })
            }
        };
    }
}

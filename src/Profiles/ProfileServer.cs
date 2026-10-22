using System.Text.Json.Nodes;

namespace Oblachka;

// Адрес сервера проверенного профиля — в одном месте для всех протоколов и обоих ядер.
// Нужен отклику (ServerPing), плану туннеля (TunnelEndpointPlan: защита WFP пропускает ядро
// только к адресам узлов) и проверке на VM.
//   Xray:     vless, vmess — settings.vnext[0]; trojan, shadowsocks — settings.servers[0]
//   sing-box: server / server_port; у wireguard (endpoint) — peers[0].address / port
public static class ProfileServer
{
    public static (string Host, int Port) Of(Profile profile) => Of(profile.Outbound, profile.Core);

    public static (string Host, int Port) Of(JsonObject outbound, CoreKind core)
    {
        var peer = Peer(outbound, core);
        var (host, port) = core == CoreKind.Xray || IsWireGuard(outbound) ? ("address", "port") : ("server", "server_port");
        return (peer[host]!.GetValue<string>(), peer[port]!.GetValue<int>());
    }

    // Подменяет имя сервера проверенным адресом (план туннеля разрешает DNS заранее).
    public static void SetHost(JsonObject outbound, CoreKind core, string address)
    {
        var peer = Peer(outbound, core);
        peer[core == CoreKind.Xray || IsWireGuard(outbound) ? "address" : "server"] = address;
    }

    // Протоколы поверх UDP/QUIC: TCP-рукопожатие о них ничего не говорит, отклик не меряем.
    public static bool IsUdp(Profile profile) => profile.Core == CoreKind.SingBox &&
        profile.Outbound["type"]?.GetValue<string>() is "hysteria2" or "tuic" or "wireguard" or "amneziawg";

    // AmneziaWG есть только в своём ядре туннеля (oblachka-core), а не в sing-box.
    public static bool IsAmnezia(Profile profile) => profile.Core == CoreKind.SingBox && profile.Outbound["type"]?.GetValue<string>() == "amneziawg";

    internal static bool IsWireGuard(JsonObject outbound) => outbound["type"]?.GetValue<string>() == "wireguard";

    private static JsonObject Peer(JsonObject outbound, CoreKind core)
    {
        if (core == CoreKind.Xray)
            return (outbound["protocol"]?.GetValue<string>() is "trojan" or "shadowsocks"
                ? outbound["settings"]!["servers"]![0] : outbound["settings"]!["vnext"]![0])!.AsObject();
        return IsWireGuard(outbound) ? outbound["peers"]![0]!.AsObject() : outbound;
    }
}

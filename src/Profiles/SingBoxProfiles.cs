using System.Text.Json.Nodes;

namespace Oblachka;

// Deliberately limited native imports: unknown settings fail, never disappear silently.
//
// Протоколы sing-box и что от них требуется:
//   vless, trojan, tuic, anytls, hysteria2 — TLS обязателен (сами не шифруют или работают поверх QUIC/TLS);
//   vmess — шифрует сам (security не none/zero), TLS по желанию;
//   shadowsocks — шифрует сам (AEAD или 2022), TLS не бывает;
//   wireguard — endpoint, а не outbound: шифрует сам, свои ключи;
//   amneziawg — выход ядра туннеля oblachka-core (core/amneziawg.go): WireGuard с обфускацией.
public static class SingBoxProfiles
{
    private static readonly string[] Types = { "vless", "hysteria2", "trojan", "vmess", "shadowsocks", "tuic", "anytls", "wireguard", "amneziawg" };

    public static bool IsSupported(JsonObject outbound) =>
        outbound["type"] is JsonValue value && value.TryGetValue<string>(out var type) && Types.Contains(type);

    public static Profile Parse(JsonObject outbound, string name)
    {
        if (!IsSupported(outbound)) throw new UserError("sing-box: поддерживаются VLESS, Trojan, VMess, Shadowsocks, Hysteria2, TUIC, AnyTLS, WireGuard и AmneziaWG.");
        var type = outbound["type"]!.GetValue<string>();
        if (type == "wireguard") return Finish(outbound, name, SingBoxProtocols.WireGuard(outbound));
        if (type == "amneziawg") return Finish(outbound, name, SingBoxProtocols.AmneziaWG(outbound));
        var common = new[] { "type", "tag", "server", "server_port", "tls", "network" };
        AllowOnly(outbound, common.Concat(SingBoxProtocols.Fields(type)).ToArray());
        var server = outbound["server"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(server) || server.Any(char.IsWhiteSpace)) throw new UserError("sing-box: нужен адрес сервера.");
        var port = outbound["server_port"]?.GetValue<int>() ?? 0;
        if (port is < 1 or > 65535) throw new UserError("sing-box: порт должен быть от 1 до 65535.");
        var secured = CheckTls(outbound, type);
        var transport = SingBoxProtocols.Check(outbound, type);
        return Finish(outbound, name, transport + (secured ? " / TLS" : ""));
    }

    // true — TLS включён. Для протоколов без своего шифрования он обязателен.
    private static bool CheckTls(JsonObject outbound, string type)
    {
        if (outbound["tls"] is not JsonObject tls || tls["enabled"]?.GetValue<bool>() != true)
        {
            if (type is "vmess" or "shadowsocks") return false;
            throw new UserError("sing-box: для " + type.ToUpperInvariant() + " требуется включённый TLS.");
        }
        if (type == "shadowsocks") throw new UserError("sing-box: Shadowsocks с TLS не поддерживается.");
        AllowOnly(tls, "enabled", "server_name", "insecure", "alpn", "min_version", "max_version", "cipher_suites",
            "curve_preferences", "certificate", "certificate_public_key_sha256", "utls", "reality", "disable_sni", "handshake_timeout");
        if (tls["insecure"]?.GetValue<bool>() == true) throw new UserError("sing-box: проверка сертификата должна быть включена.");
        if (tls["utls"] is JsonObject utls) AllowOnly(utls, "enabled", "fingerprint");
        if (tls["reality"] is JsonObject reality) AllowOnly(reality, "enabled", "public_key", "short_id");
        return true;
    }

    private static Profile Finish(JsonObject outbound, string name, string transport)
    {
        var copy = (JsonObject)outbound.DeepClone();
        copy["tag"] = "tunnel";
        var type = copy["type"]!.GetValue<string>();
        var safeName = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        return new Profile(safeName.Length == 0 ? type.ToUpperInvariant() + "-профиль" : safeName, transport, copy, CoreKind.SingBox);
    }

    internal static void AllowOnly(JsonObject options, params string[] fields)
    {
        if (options.Any(pair => !fields.Contains(pair.Key, StringComparer.Ordinal)))
            throw new UserError("sing-box: профиль содержит неподдерживаемые настройки. Экспортируйте один outbound без внешних файлов и цепочек.");
    }
}

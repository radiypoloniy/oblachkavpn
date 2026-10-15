using System.Text.Json.Nodes;

namespace Oblachka;

// Deliberately limited native imports: unknown settings fail, never disappear silently.
public static class SingBoxProfiles
{
    public static bool IsSupported(JsonObject outbound) =>
        outbound["type"] is JsonValue value && value.TryGetValue<string>(out var type) && type is "vless" or "hysteria2";

    public static Profile Parse(JsonObject outbound, string name)
    {
        if (!IsSupported(outbound)) throw new UserError("sing-box: поддерживаются нативные VLESS и Hysteria2 outbounds.");
        var type = outbound["type"]!.GetValue<string>();
        var common = new[] { "type", "tag", "server", "server_port", "tls", "network" };
        var specific = type == "vless"
            ? new[] { "uuid", "flow", "transport", "packet_encoding" }
            : new[] { "password", "up_mbps", "down_mbps", "obfs", "server_ports", "hop_interval", "hop_interval_max" };
        AllowOnly(outbound, common.Concat(specific).ToArray());
        var server = outbound["server"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(server) || server.Any(char.IsWhiteSpace)) throw new UserError("sing-box: нужен адрес сервера.");
        var port = outbound["server_port"]?.GetValue<int>() ?? 0;
        if (port is < 1 or > 65535) throw new UserError("sing-box: порт должен быть от 1 до 65535.");
        if (outbound["tls"] is not JsonObject tls || tls["enabled"]?.GetValue<bool>() != true)
            throw new UserError("sing-box: требуется включённый TLS.");
        AllowOnly(tls, "enabled", "server_name", "insecure", "alpn", "min_version", "max_version", "cipher_suites",
            "curve_preferences", "certificate", "certificate_public_key_sha256", "utls", "reality", "disable_sni", "handshake_timeout");
        if (tls["insecure"]?.GetValue<bool>() == true) throw new UserError("sing-box: проверка сертификата должна быть включена.");
        if (tls["utls"] is JsonObject utls) AllowOnly(utls, "enabled", "fingerprint");
        if (tls["reality"] is JsonObject reality) AllowOnly(reality, "enabled", "public_key", "short_id");
        var transport = "TCP";
        if (type == "vless")
        {
            if (!Guid.TryParse(outbound["uuid"]?.GetValue<string>(), out _)) throw new UserError("sing-box: нужен UUID VLESS.");
            if (outbound["transport"] is JsonObject options)
            {
                var transportType = options["type"]?.GetValue<string>();
                var fields = transportType switch
                {
                    "ws" => new[] { "type", "path", "headers", "max_early_data", "early_data_header_name" },
                    "grpc" => new[] { "type", "service_name", "idle_timeout", "ping_timeout", "permit_without_stream" },
                    "httpupgrade" => new[] { "type", "host", "path", "headers" },
                    "http" => new[] { "type", "host", "path", "method", "headers", "idle_timeout", "ping_timeout" },
                    _ => throw new UserError("sing-box: этот транспорт не поддерживается. XHTTP импортируй как Xray JSON.")
                };
                AllowOnly(options, fields);
                transport = transportType!.ToUpperInvariant();
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(outbound["password"]?.GetValue<string>())) throw new UserError("sing-box: нужен пароль Hysteria2.");
            if (outbound["obfs"] is JsonObject obfs)
            {
                AllowOnly(obfs, "type", "password");
                if (obfs["type"]?.GetValue<string>() != "salamander" || string.IsNullOrWhiteSpace(obfs["password"]?.GetValue<string>()))
                    throw new UserError("sing-box: в этой сборке поддерживается obfs salamander с паролем.");
            }
            transport = "HYSTERIA2 / QUIC";
        }
        var copy = (JsonObject)outbound.DeepClone();
        copy["tag"] = "tunnel";
        var safeName = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        return new Profile(safeName.Length == 0 ? type.ToUpperInvariant() + "-профиль" : safeName,
            transport + " / TLS", copy, CoreKind.SingBox);
    }

    private static void AllowOnly(JsonObject options, params string[] fields)
    {
        if (options.Any(pair => !fields.Contains(pair.Key, StringComparer.Ordinal)))
            throw new UserError("sing-box: профиль содержит неподдерживаемые настройки. Экспортируй один outbound без внешних файлов и цепочек.");
    }
}

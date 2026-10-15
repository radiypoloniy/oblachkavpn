using System.Text.Json.Nodes;

namespace Oblachka;

// Проверка VMess и Shadowsocks в формате Xray (VLESS и Trojan — в Profiles). Допускается только
// надёжное шифрование: у VMess отклоняются security none/zero, у Shadowsocks — всё, кроме AEAD и 2022.
public static class XrayProtocols
{
    // Методы Shadowsocks, которые принимаются в обоих ядрах. Старые потоковые (rc4, cfb, table)
    // и «без шифрования» отклоняются: их трафик читается или подделывается.
    public static readonly string[] ShadowsocksMethods =
    {
        "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305"
    };

    public static readonly string[] VmessSecurity = { "auto", "aes-128-gcm", "chacha20-poly1305" };

    public static void Validate(string protocol, JsonObject settings)
    {
        if (protocol == "vmess")
        {
            var vnext = settings["vnext"] as JsonArray;
            if (vnext?.Count != 1 || vnext[0] is not JsonObject peer || peer["users"] is not JsonArray users || users.Count != 1 || users[0] is not JsonObject user)
                throw new UserError("Для VMess нужны settings.vnext с одним сервером и одним пользователем.");
            Endpoint(peer);
            if (!Guid.TryParse(user["id"]?.GetValue<string>(), out _)) throw new UserError("В профиле VMess нужен UUID.");
            CheckVmess(user["security"]?.GetValue<string>(), user["alterId"]?.GetValue<int>() ?? 0);
        }
        else
        {
            if (settings["servers"] is not JsonArray servers || servers.Count != 1 || servers[0] is not JsonObject server)
                throw new UserError("Для Shadowsocks нужен settings.servers с одним сервером.");
            Endpoint(server);
            CheckShadowsocks(server["method"]?.GetValue<string>(), server["password"]?.GetValue<string>());
        }
    }

    public static void CheckVmess(string? security, int alterId)
    {
        if (!VmessSecurity.Contains(security ?? "auto", StringComparer.OrdinalIgnoreCase))
            throw new UserError("VMess без шифрования (none, zero) не принимается.");
        // alterId > 0 — устаревшая схема с MD5, отключённая в обоих ядрах.
        if (alterId != 0) throw new UserError("VMess с alterId больше нуля устарел и не поддерживается ядрами. Попросите у сервиса новую ссылку.");
    }

    public static void CheckShadowsocks(string? method, string? password)
    {
        if (string.IsNullOrEmpty(method) || !ShadowsocksMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
            throw new UserError("Этот метод шифрования Shadowsocks устарел или ненадёжен. Поддерживаются AEAD (aes-gcm, chacha20) и Shadowsocks 2022.");
        if (string.IsNullOrEmpty(password)) throw new UserError("В профиле Shadowsocks нет пароля.");
    }

    private static void Endpoint(JsonObject peer)
    {
        var address = peer["address"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace)) throw new UserError("Не указан корректный адрес сервера.");
        if ((peer["port"]?.GetValue<int>() ?? 0) is < 1 or > 65535) throw new UserError("Порт сервера должен быть от 1 до 65535.");
    }
}

using System.Security.Cryptography;

namespace Oblachka;

// Поведение в сети (как Network Options у Windscribe и доверенные сети у NordVPN):
//   Usual   — как настроено в приложении («Сразу подключаться», незнакомые сети);
//   Vpn     — VPN включается сам, как только компьютер в этой сети, и при запуске в ней;
//   Trusted — доверенная: сам VPN здесь не включается ни при запуске, ни как незнакомая сеть.
// Выключать работающий VPN сеть не может: это решает только человек.
public enum NetworkMode { Usual, Vpn, Trusted }

// Сеть, о которой человек что-то решил. Ключ — хеш идентификатора сети Windows, имя — для подписи.
public sealed record KnownNetwork(string Name, NetworkMode Mode);

public static class NetworkPolicy
{
    public const int MaxKnown = 30, MaxName = 48;

    // В файл состояния — не идентификатор Windows, а его хеш.
    public static string Key(Guid id) => Convert.ToHexString(SHA256.HashData(id.ToByteArray()))[..16];

    public static bool IsKey(string? key) => key is { Length: 16 } && key.All(char.IsAsciiHexDigit);

    // Имя сети чужое (SSID задаёт кто угодно): без управляющих символов и не длиннее подписи.
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? "Без имени" : clean.Length > MaxName ? clean[..MaxName] + "…" : clean;
    }

    // Включить ли VPN, когда компьютер оказался в сети. Незнакомая — о которой человек ничего не решал;
    // «общественной» её считает Windows (так она помечает сети, где компьютер не виден другим).
    public static bool ShouldConnect(NetworkMode mode, bool known, bool isPublic, bool secureUnknown, bool vpnOn, bool paused, bool stoppedHere) =>
        !vpnOn && !paused && !stoppedHere && mode != NetworkMode.Trusted &&
        (mode == NetworkMode.Vpn || !known && isPublic && secureUnknown);

    // Снимок проверяется так же строго, как любые сохранённые данные.
    public static Dictionary<string, KnownNetwork> Restore(Dictionary<string, KnownNetwork>? saved) =>
        (saved ?? []).Where(p => IsKey(p.Key) && p.Value is { Mode: NetworkMode.Vpn or NetworkMode.Trusted })
            .Take(MaxKnown).ToDictionary(p => p.Key, p => p.Value with { Name = CleanName(p.Value.Name) });
}

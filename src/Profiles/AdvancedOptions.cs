using System.Net;

namespace Oblachka;

// «Для продвинутых»: тонкая настройка для тех, кто пришёл из Throne, v2rayN или Happ. Значение по
// умолчанию у каждого поля — «как без настройки» (null, пусто, Auto): незаполненный раздел даёт
// ровно ту конфигурацию, что и до него. Значения проверяются здесь и ещё раз службой (TunnelTuning),
// потому что часть их уходит в ядро туннеля, работающее с правами системы.
public enum TunnelStack { Auto, Gvisor, Mixed, System }

public sealed record AdvancedOptions
{
    public static readonly AdvancedOptions Default = new();

    // Туннель: стек и MTU; Auto — как сейчас (gvisor/9000, с «Совместимостью с играми» — mixed/1500).
    public TunnelStack Stack { get; init; }
    public int? Mtu { get; init; }
    // DNS через VPN вместо встроенного DoH Cloudflare: https://…/dns-query, tls://хост, quic://хост.
    public string? TunnelDns { get; init; }
    // Подсети мимо туннеля, CIDR по одной: рабочая сеть, VPN работодателя.
    public string[] BypassSubnets { get; init; } = [];
    // Обход DPI «Всегда»: своя форма фрагментации вместо заданной провайдером или по умолчанию.
    public string? FragmentPackets { get; init; }
    public string? FragmentLength { get; init; }
    public string? FragmentInterval { get; init; }
    // User-Agent для сторонних подписок: некоторые панели отдают список только «своим» клиентам.
    public string? SubscriptionUserAgent { get; init; }
    // Адрес проверки связи вместо microsoft.com и cloudflare.com.
    public string? ProbeUrl { get; init; }
    // Постоянный порт локального прокси для программ; null — свободный, какой найдётся.
    public int? ProxyPort { get; init; }

    public const int MinMtu = 1280, MaxMtu = 9000, MaxSubnets = 32;

    public bool IsDefault => Stack == TunnelStack.Auto && Mtu is null && TunnelDns is null && BypassSubnets.Length == 0 &&
        FragmentPackets is null && FragmentLength is null && FragmentInterval is null && SubscriptionUserAgent is null && ProbeUrl is null && ProxyPort is null;

    // Ошибка — человеческим текстом для поля; null — всё верно.
    public static string? CheckMtu(int? mtu) => mtu is null or (>= MinMtu and <= MaxMtu) ? null : $"MTU — от {MinMtu} до {MaxMtu}.";

    public static string? CheckDns(string? dns)
    {
        if (dns is null) return null;
        if (!Uri.TryCreate(dns, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "tls" or "quic") || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
            return "Нужен адрес вида https://dns.example/dns-query, tls://dns.example или quic://dns.example.";
        return null;
    }

    public static string? CheckSubnets(IReadOnlyList<string> subnets)
    {
        if (subnets.Count > MaxSubnets) return $"Не больше {MaxSubnets} подсетей.";
        foreach (var subnet in subnets)
        {
            if (!TryParseSubnet(subnet, out var address, out var prefix)) return $"«{subnet}» — не подсеть. Пример: 10.20.0.0/16.";
            // Подсеть шире /8 уводит мимо VPN почти весь интернет — это уже не «рабочая сеть».
            var min = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 8 : 16;
            if (prefix < min) return $"«{subnet}» слишком широкая: мимо VPN ушла бы большая часть интернета.";
        }
        return null;
    }

    public static bool TryParseSubnet(string text, out IPAddress address, out int prefix)
    {
        address = IPAddress.None; prefix = 0;
        var parts = text.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var parsed) || !int.TryParse(parts[1], out prefix)) return false;
        address = parsed;
        return prefix >= 0 && prefix <= (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }

    public static string? CheckFragment(string? packets, string? length, string? interval) =>
        packets is not null && !ProviderOptions.IsPackets(packets) ? "Куски: tlshello или диапазон 1-3." :
        length is not null && !ProviderOptions.IsLength(length) ? "Длина: диапазон байт, например 50-100." :
        interval is not null && !ProviderOptions.IsInterval(interval) ? "Пауза: миллисекунды, например 5-10." : null;

    // Только печатные ASCII: иначе HttpClient не отправит заголовок.
    public static string? CheckUserAgent(string? agent) =>
        agent is null || agent.Length <= 200 && agent.All(c => c is >= ' ' and <= '~') ? null : "User-Agent — латиница, цифры и знаки, до 200 символов.";

    public static string? CheckProbe(string? url) =>
        url is null || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 ? null : "Нужен адрес https://…";

    public static string? CheckPort(int? port) => port is null or (>= 1024 and <= 65535) ? null : "Порт — от 1024 до 65535.";

    // Весь набор: так снимок проверяется при чтении (испорченное значение — как без настройки).
    public string? Problem() => CheckMtu(Mtu) ?? CheckDns(TunnelDns) ?? CheckSubnets(BypassSubnets) ?? CheckFragment(FragmentPackets, FragmentLength, FragmentInterval) ??
        CheckUserAgent(SubscriptionUserAgent) ?? CheckProbe(ProbeUrl) ?? CheckPort(ProxyPort);

    // Названия изменённых настроек — для отчёта поддержке; значения туда не идут.
    public string[] Changed() => new (bool On, string Name)[]
    {
        (Stack != TunnelStack.Auto, "Stack:" + Stack), (Mtu is not null, "Mtu:" + Mtu), (TunnelDns is not null, "TunnelDns:" + (TunnelDns is { } d ? new Uri(d).Scheme : "")),
        (BypassSubnets.Length > 0, "BypassSubnets:" + BypassSubnets.Length), ((FragmentPackets ?? FragmentLength ?? FragmentInterval) is not null, "Fragment"),
        (SubscriptionUserAgent is not null, "SubscriptionUserAgent"), (ProbeUrl is not null, "ProbeUrl"), (ProxyPort is not null, "ProxyPort")
    }.Where(c => c.On).Select(c => c.Name).ToArray();

    // Своя форма фрагментации поверх провайдерской: незаданное — из неё же или по умолчанию.
    public ProviderFragment? Fragment(ProviderFragment? provider)
    {
        if (FragmentPackets is null && FragmentLength is null && FragmentInterval is null) return provider;
        var basis = provider ?? ProviderFragment.Default;
        return basis with { Enabled = true, Packets = FragmentPackets ?? basis.Packets, Length = FragmentLength ?? basis.Length, Interval = FragmentInterval ?? basis.Interval };
    }
}

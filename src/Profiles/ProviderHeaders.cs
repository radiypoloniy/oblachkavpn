using System.Net;
using System.Text.RegularExpressions;

namespace Oblachka;

// Как провайдер управляет клиентом заголовками подписки. Имена — как у Happ и INCY, чтобы
// одна настройка панели Remnawave работала во всех трёх клиентах:
//   new-url / new-domain        — подписка переехала (адрес проверяется загрузкой, старый остаётся запасным)
//   fallback-url                — запасной адрес подписки
//   premium-url, sub-expire-button-link, expiry-banner-button-url — куда ведёт «Продлить»
//   announce-url, banner-*      — объявление с кнопкой
//   server-address-resolve-*    — адреса серверов через DoH, если DNS провайдера врёт
//   fragmentation-*             — обход DPI для всех клиентов подписки
// Всё приходит от сервера: значения проверяются строго, непонятное отбрасывается молча.
public sealed record ProviderOptions
{
    public string? NewUrl { get; init; }
    public string? FallbackUrl { get; init; }
    public string? RenewUrl { get; init; }
    public string? AnnounceUrl { get; init; }
    public string? AnnounceButton { get; init; }
    public ServerResolve? Resolve { get; init; }
    public ProviderFragment? Fragment { get; init; }

    public const int MaxButtonLength = 25;
    private static readonly Regex Range = new(@"^\d{1,5}(-\d{1,5})?$", RegexOptions.CultureInvariant);
    private static readonly Regex Packets = new(@"^(tlshello|\d{1,2}(-\d{1,2})?)$", RegexOptions.CultureInvariant);

    public bool IsEmpty => this == new ProviderOptions();

    public static ProviderOptions? Read(Func<string, string?> header, Uri source)
    {
        var options = new ProviderOptions
        {
            NewUrl = SubscriptionMetadata.ParseLink(header("new-url")) ?? NewDomain(header("new-domain"), source),
            FallbackUrl = SubscriptionMetadata.ParseLink(header("fallback-url")),
            RenewUrl = First(header, "premium-url", "sub-expire-button-link", "expiry-banner-button-url", "expiry-banner-url"),
            AnnounceUrl = First(header, "announce-url", "banner-button-url", "banner-url"),
            AnnounceButton = Button(header("banner-button-text")),
            Resolve = ServerResolve.Read(header),
            Fragment = ProviderFragment.Read(header),
        };
        // Тот же адрес, что и загруженный, — не переезд.
        if (options.NewUrl == source.AbsoluteUri) options = options with { NewUrl = null };
        return options.IsEmpty ? null : options;
    }

    private static string? First(Func<string, string?> header, params string[] names) =>
        names.Select(n => SubscriptionMetadata.ParseLink(header(n))).FirstOrDefault(l => l is not null);

    private static string? Button(string? value) =>
        SubscriptionMetadata.DecodeText(value) is { } text ? text.Length > MaxButtonLength ? text[..MaxButtonLength] : text : null;

    // new-domain меняет только хост: путь с ключом остаётся прежним.
    private static string? NewDomain(string? value, Uri source)
    {
        var host = value?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host) || host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.')) return null;
        return SubscriptionMetadata.ParseLink(new UriBuilder(source) { Host = host, Port = -1 }.Uri.AbsoluteUri);
    }

    internal static bool IsRange(string? value) => value is not null && Range.IsMatch(value) &&
        value.Split('-').Select(int.Parse).ToArray() is var n && (n.Length == 1 || n[0] <= n[1]);
    internal static bool IsPackets(string? value) => value is not null && Packets.IsMatch(value);

    // Снимок проверяется так же строго, как пришедшее от сервера.
    public static void Validate(ProviderOptions options)
    {
        foreach (var link in new[] { options.NewUrl, options.FallbackUrl, options.RenewUrl, options.AnnounceUrl })
            if (link is not null && SubscriptionMetadata.ParseLink(link) != link) throw new UserError("Ссылки подписки повреждены.");
        if (options.AnnounceButton is { } button && (button.Length > MaxButtonLength || button.Any(char.IsControl)))
            throw new UserError("Сведения подписки повреждены.");
        if (options.Resolve is { } resolve && ServerResolve.Create(resolve.DohUrl, resolve.Bootstrap) != resolve)
            throw new UserError("Настройки DNS подписки повреждены.");
        if (options.Fragment is { } fragment && (!IsPackets(fragment.Packets) || !IsRange(fragment.Length) || !IsRange(fragment.Interval)))
            throw new UserError("Настройки обхода DPI подписки повреждены.");
    }

    internal static bool Flag(string? value) => value?.Trim().ToLowerInvariant() is "1" or "true";
}

// DoH для адресов серверов: адрес DoH и IP, по которому к нему подключаться (без него имя
// самого DoH пришлось бы разрешать тем же DNS, которому не верим).
public sealed record ServerResolve(string DohUrl, string Bootstrap)
{
    public static ServerResolve? Read(Func<string, string?> header)
    {
        if (!ProviderOptions.Flag(header("server-address-resolve-enable"))) return null;
        return Create(header("server-address-resolve-dns-domain")?.Trim(), header("server-address-resolve-dns-ip")?.Trim())
            ?? Create(DohResolver.Cloudflare.DohUrl, DohResolver.Cloudflare.Bootstrap);
    }

    public static ServerResolve? Create(string? url, string? ip)
    {
        if (SubscriptionMetadata.ParseLink(url) is not { } link || !IPAddress.TryParse(ip, out var address)) return null;
        // Bootstrap — публичный адрес: в локальную сеть и на себя DoH не ходит.
        if (IPAddress.IsLoopback(address) || DohResolver.IsPrivate(address)) return null;
        return new(link, address.ToString());
    }
}

// Фрагментация TLS-рукопожатия по просьбе провайдера. Длины и паузы — диапазоны «50-100».
public sealed record ProviderFragment(bool Enabled, string Packets, string Length, string Interval)
{
    public static ProviderFragment? Read(Func<string, string?> header)
    {
        var enable = header("fragmentation-enable");
        if (string.IsNullOrWhiteSpace(enable)) return null;
        string Pick(string name, string fallback, Func<string?, bool> valid) =>
            header(name)?.Trim().ToLowerInvariant() is var v && valid(v) ? v! : fallback;
        return new(ProviderOptions.Flag(enable),
            Pick("fragmentation-packets", "tlshello", ProviderOptions.IsPackets),
            Pick("fragmentation-length", "50-100", ProviderOptions.IsRange),
            Pick("fragmentation-interval", "5-10", ProviderOptions.IsRange));
    }
    public static ProviderFragment Default { get; } = new(true, "tlshello", "50-100", "5-10");
}

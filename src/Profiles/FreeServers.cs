namespace Oblachka;

public enum FreeList { Regular, Mobile, Whitelist }

// Бесплатные серверы «на свой риск» (решение владельца 24.10.2026, _internal/docs/FREE-KEYS-PLAN):
// открытые списки чужих узлов на GitHub. Адреса вшиты и из окна не меняются: люди не знают, где
// такие списки искать, а подставить свой источник — значит подставить свои узлы.
//
// Узлы держатся только в памяти: на диск не пишутся, после перезапуска их нет. Они живут часы,
// к вчерашнему узлу подключаться незачем, и сами при запуске они не включаются. В автовыбор и в
// запасные они не входят никогда (AutoPool, RouteCandidates), заслоны — ForeignGuard.
public static class FreeServers
{
    public const string GroupId = "free";
    public const string GroupName = "Бесплатные";
    // Показываются лучшие по задержке из ответивших.
    public const int Shown = 15;
    // Столько серверов проверяется настоящим запросом через них: ~4 одновременно, по нескольку секунд каждый.
    public const int Checked = 40;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private const string Igareck = "igareck/vpn-configs-for-russia/main/";

    // Файлы — владелец/репозиторий/ветка/путь. Разведка источников — NEXT-STEPS, раздел 5.
    public static IReadOnlyList<string> Files(FreeList list) => list switch
    {
        FreeList.Mobile => [Igareck + "BLACK_VLESS_RUS_mobile.txt"],
        FreeList.Whitelist => [Igareck + "Vless-Reality-White-Lists-Rus-Mobile.txt", "zieng2/wl/main/vless_universal.txt"],
        _ => [Igareck + "BLACK_VLESS_RUS.txt"]
    };

    // Основной адрес и запасной: jsDelivr отдаёт тот же файл (кэш до 12 ч — лучше пустого списка).
    public static string[] Urls(string file)
    {
        var parts = file.Split('/', 4);
        return ["https://raw.githubusercontent.com/" + file, $"https://cdn.jsdelivr.net/gh/{parts[0]}/{parts[1]}@{parts[2]}/{parts[3]}"];
    }

    public static string Label(FreeList list) => list switch
    {
        FreeList.Mobile => "Мобильный", FreeList.Whitelist => "Белые списки", _ => "Обычный"
    };

    // Списки бывают длиннее лимита подписки: берутся первые ссылки, остальное отбрасывается.
    public static string Trim(string body)
    {
        if (!body.Contains("://", StringComparison.Ordinal)) return body;
        return string.Join('\n', body.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("://", StringComparison.Ordinal)).Take(Subscriptions.MaxProfiles));
    }

    // Узлы одного списка: основной адрес, при отказе — запасной. Напрямую (без SOCKS ядра), HTTPS
    // без перенаправлений, не больше 1 МБ; никаких сведений об устройстве в запросе.
    public static async Task<IReadOnlyList<Profile>> FetchAsync(string file, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = Profiles.MaxBytes };
        foreach (var url in Urls(file))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(ServiceBrand.UserAgent);
                using var response = await client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode) continue;
                var body = await response.Content.ReadAsStringAsync(token);
                return Subscriptions.Parse(Trim(body)).Profiles;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UserError or IOException) { }
        }
        return [];
    }

    // Повторы — один сервер (адрес и порт) под разными подписями: остаётся первый. Подписи из
    // списков (эмодзи, реклама, ссылки на каналы) не показываются и не хранятся — только своя:
    // «Бесплатный · Финляндия · 3». Флага в имени нет: WPF в Windows флаги-эмодзи не рисует, а страну
    // CountryCatalog находит и по русскому названию.
    public static IReadOnlyList<Profile> Prepare(IEnumerable<Profile> parsed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<Profile>();
        foreach (var profile in parsed)
        {
            var key = ServerPing.Endpoint(profile) is { } endpoint ? endpoint.Host + ":" + endpoint.Port : Profiles.Fingerprint(profile);
            if (seen.Add(key)) unique.Add(profile);
        }
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        return unique.Select(profile =>
        {
            var country = profile.Country;
            var where = country.Code is { Length: 2 } ? country.Name : "страна неизвестна";
            numbers[where] = numbers.GetValueOrDefault(where) + 1;
            return profile with { Name = "Бесплатный · " + where + " · " + numbers[where], Source = ProfileSource.Free, Group = GroupId };
        }).ToArray();
    }


}

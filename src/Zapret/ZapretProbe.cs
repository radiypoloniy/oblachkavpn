using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace Oblachka;

// Работает ли обход: настоящие HTTPS-запросы к YouTube и Discord (цели — из набора Flowseal).
// Сайт считается доступным, если ответ пришёл и тело дочиталось или пришло больше 24 КБ:
// фильтры часто пропускают начало и замирают на ~16 КБ — короткий ответ этого не покажет.
public sealed record ZapretProbeResult(int Passed, int Total, long Milliseconds)
{
    // Открылся ли каждый адрес из Targets — по ним решаются группы (ZapretPlan).
    public IReadOnlyList<bool> Ok { get; init; } = Array.Empty<bool>();
    // Подробности по каждому адресу — для журнала zapret (ZapretJournal) и отчёта поддержке.
    public IReadOnlyList<ZapretTargetResult> Details { get; init; } = Array.Empty<ZapretTargetResult>();
    // Адреса, открывшиеся только по QUIC (как у браузера), а по TCP — нет.
    public IReadOnlyList<int> QuicOnly { get; init; } = Array.Empty<int>();
    public bool All => Passed == Total;
}

// Что случилось с одним адресом проверки: сколько пришло, за сколько и почему не открылся.
public enum ZapretFailure { None, Timeout, Slow, Refused, Tls }
public sealed record ZapretTargetResult(bool Ok, long Bytes, long Milliseconds, ZapretFailure Failure)
{
    public bool Quic { get; init; }
}

public static class ZapretProbe
{
    public static readonly Uri[] Targets =
    {
        new("https://www.youtube.com/"), new("https://i.ytimg.com/"), new("https://redirector.googlevideo.com/report_mapping"),
        new("https://discord.com/"), new("https://gateway.discord.gg/"), new("https://cdn.discordapp.com/")
    };

    private const int Enough = 24 * 1024, Limit = 64 * 1024;

    // Адрес видео (googlevideo.com) провайдер не блокирует, а замедляет: первые килобайты идут,
    // дальше скорость падает до сотен килобит. Страница и превью открывались, проверка 64 КБ
    // проходила, а видео не играло (0.14.0). Поэтому с него нужно скачать полмегабайта
    // не медленнее, чем нужно для 720p (около 2,5 Мбит/с).
    public static readonly int[] Heavy = [2];
    public const int HeavyBytes = 512 * 1024, HeavyLimit = 1024 * 1024;
    public const long HeavyBytesPerSecond = 300 * 1024;

    // Видео YouTube браузер берёт по QUIC (UDP 443), а провайдер режет googlevideo.com именно
    // по TCP — стратегии Flowseal обходят и то, и другое. Адрес видео засчитывается, если он
    // открылся хотя бы одним способом: проверка «только TCP» оставляла YouTube в VPN, хотя
    // в браузере он работал (0.13.3-rc.4, redirector.googlevideo.com).
    public static readonly int[] QuicFallback = [2];

    // via — вход прямого трафика ядра туннеля (под «Весь компьютер»: тот же путь, которым пойдут
    // сайты zapret, — выход ядра на физическую сеть через winws, DNS сети); null — напрямую из окна.
    // Раньше проверка шла через SOCKS ядра окна: с 0.17 у него нет списков (мерила VPN), а при
    // выходе прямо на сервер его нет вовсе (не открывалось ничего).
    public static async Task<ZapretProbeResult> RunAsync(SocksRoute? via, CancellationToken token, IReadOnlyList<Uri>? targets = null)
    {
        var list = targets ?? Targets;
        var watch = Stopwatch.StartNew();
        using var handler = new SocketsHttpHandler
        {
            UseProxy = via is not null,
            Proxy = via is null ? null : new WebProxy($"socks5://127.0.0.1:{via.Port}", false)
                { Credentials = via.User is null ? null : new NetworkCredential(via.User, via.Password) },
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.Zero
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        var details = await Task.WhenAll(list.Select((t, i) => ReachAsync(client, t, token, targets is null && Heavy.Contains(i)))).ConfigureAwait(false);
        var results = details.Select(d => d.Ok).ToArray();
        var quic = new List<int>();
        // QUIC — только без VPN: под «Весь компьютер» HTTP/3 из окна ушёл бы в туннель, а не напрямую.
        if (targets is null && via is null && System.Net.Quic.QuicConnection.IsSupported)
            // Только если остальные адреса группы открылись: иначе группа всё равно не пройдёт,
            // а лишняя попытка удлинила бы каждый шаг подбора.
            foreach (var i in QuicFallback.Where(i => !results[i] && ZapretPlan.Groups.First(g => g.Targets.Contains(i)).Targets.All(j => j == i || results[j])))
                if (await ReachQuicAsync(list[i], token, Heavy.Contains(i)).ConfigureAwait(false) is { Ok: true } viaQuic) { results[i] = true; details[i] = viaQuic with { Quic = true }; quic.Add(i); }
        return new(results.Count(r => r), list.Count, watch.ElapsedMilliseconds) { Ok = results, QuicOnly = quic, Details = details };
    }

    // HTTP/3 без прокси — по маршрутам Windows, как у браузера (только без VPN).
    private static async Task<ZapretTargetResult> ReachQuicAsync(Uri target, CancellationToken token, bool heavy)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestVersion = HttpVersion.Version30, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        return await ReachAsync(client, target, token, heavy).ConfigureAwait(false);
    }

    // heavy — адрес видео: нужен объём и скорость (HeavyBytes, HeavyBytesPerSecond), а не просто ответ.
    private static async Task<ZapretTargetResult> ReachAsync(HttpClient client, Uri target, CancellationToken token, bool heavy = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(heavy ? 8 : 7));
        var total = 0;
        var limit = heavy ? HeavyLimit : Limit;
        var clock = Stopwatch.StartNew();
        var started = Stopwatch.StartNew();
        ZapretTargetResult Result(bool ok, ZapretFailure failure) => new(ok, total, started.ElapsedMilliseconds, ok ? ZapretFailure.None : failure);
        try
        {
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[16384];
            int read;
            clock.Restart();
            while (total < limit && (read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0) total += read;
            return Result(!heavy || FastEnough(total, clock.Elapsed), ZapretFailure.Slow);
        }
        // Замерло: доступен, только если успели прийти больше ~16 КБ, на которых режут фильтры.
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Result(heavy ? FastEnough(total, clock.Elapsed) : total >= Enough, total > 0 ? ZapretFailure.Slow : ZapretFailure.Timeout); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Security.Authentication.AuthenticationException)
        { return Result(false, ex is System.Security.Authentication.AuthenticationException || ex.InnerException is System.Security.Authentication.AuthenticationException ? ZapretFailure.Tls : ZapretFailure.Refused); }
    }

    // Видео играет: пришло не меньше полмегабайта и не медленнее порога.
    public static bool FastEnough(long bytes, TimeSpan elapsed) =>
        bytes >= HeavyBytes && bytes / Math.Max(elapsed.TotalSeconds, 0.05) >= HeavyBytesPerSecond;
}

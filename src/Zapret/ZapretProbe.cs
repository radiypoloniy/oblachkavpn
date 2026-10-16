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
    // Адреса, открывшиеся только по QUIC (как у браузера), а по TCP — нет.
    public IReadOnlyList<int> QuicOnly { get; init; } = Array.Empty<int>();
    public bool All => Passed == Total;
}

public static class ZapretProbe
{
    public static readonly Uri[] Targets =
    {
        new("https://www.youtube.com/"), new("https://i.ytimg.com/"), new("https://redirector.googlevideo.com/report_mapping"),
        new("https://discord.com/"), new("https://gateway.discord.gg/"), new("https://cdn.discordapp.com/")
    };

    private const int Enough = 24 * 1024, Limit = 64 * 1024;

    // Видео YouTube браузер берёт по QUIC (UDP 443), а провайдер режет googlevideo.com именно
    // по TCP — стратегии Flowseal обходят и то, и другое. Адрес видео засчитывается, если он
    // открылся хотя бы одним способом: проверка «только TCP» оставляла YouTube в VPN, хотя
    // в браузере он работал (0.13.3-rc.4, redirector.googlevideo.com).
    public static readonly int[] QuicFallback = [2];

    // proxyPort — проверять через локальный прокси ядра (под «Весь компьютер» эти сайты идут
    // напрямую через ядро); null — напрямую из окна.
    public static async Task<ZapretProbeResult> RunAsync(int? proxyPort, CancellationToken token, IReadOnlyList<Uri>? targets = null)
    {
        var list = targets ?? Targets;
        var watch = Stopwatch.StartNew();
        using var handler = new SocketsHttpHandler
        {
            UseProxy = proxyPort is not null, Proxy = proxyPort is { } port ? new WebProxy($"socks5://127.0.0.1:{port}", false) : null,
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.Zero
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        var results = await Task.WhenAll(list.Select(t => ReachAsync(client, t, token))).ConfigureAwait(false);
        var quic = new List<int>();
        if (targets is null && System.Net.Quic.QuicConnection.IsSupported)
            // Только если остальные адреса группы открылись: иначе группа всё равно не пройдёт,
            // а лишняя попытка удлинила бы каждый шаг подбора.
            foreach (var i in QuicFallback.Where(i => !results[i] && ZapretPlan.Groups.First(g => g.Targets.Contains(i)).Targets.All(j => j == i || results[j])))
                if (await ReachQuicAsync(list[i], token).ConfigureAwait(false)) { results[i] = true; quic.Add(i); }
        return new(results.Count(r => r), list.Count, watch.ElapsedMilliseconds) { Ok = results, QuicOnly = quic };
    }

    // HTTP/3 без прокси — по маршрутам Windows, как у браузера. Под «Весь компьютер» это туннель
    // и ядро, которое отправляет сайты zapret напрямую; SOCKS ядра UDP для HTTP/3 не несёт.
    private static async Task<bool> ReachQuicAsync(Uri target, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestVersion = HttpVersion.Version30, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        return await ReachAsync(client, target, token).ConfigureAwait(false);
    }

    private static async Task<bool> ReachAsync(HttpClient client, Uri target, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        var total = 0;
        try
        {
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[16384];
            int read;
            while (total < Limit && (read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0) total += read;
            return true;
        }
        // Замерло: доступен, только если успели прийти больше ~16 КБ, на которых режут фильтры.
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return total >= Enough; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Security.Authentication.AuthenticationException) { return false; }
    }
}

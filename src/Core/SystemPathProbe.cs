using System.Diagnostics;
using System.Net.Http;

namespace Oblachka;

// Проверка пути, которым ходят обычные программы: без прокси, по маршрутам Windows. Под
// «Весь компьютер» SOCKS ядра может отвечать, а трафик компьютера — нет: в 0.13.1 защита
// резала пакеты zapret, и окно писало «Защищено» при мёртвом интернете. Окно — обычная
// программа, его запрос идёт тем же путём, что у браузера: туннель, защита, «напрямую».
public static class SystemPathProbe
{
    // Сайты, которые «Российские сайты напрямую» отправляют мимо VPN: проверка прямого пути.
    public static readonly Uri[] DirectTargets = [new("https://ya.ru/"), new("https://mail.ru/")];

    public static async Task<ProbeResult> RunAsync(Uri target, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.Zero
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, token);
            // Любой ответ сайта доказывает, что путь работает; код ответа здесь не важен.
            return new(true, watch.ElapsedMilliseconds, "Запрос программы через систему выполнен.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(false, watch.ElapsedMilliseconds, "Запрос программы через систему не прошёл.", ProbeFailure.Reachability);
        }
    }
}

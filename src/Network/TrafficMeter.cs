using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Oblachka;

// Скорость и трафик ядра туннеля из его локального интерфейса (clash API /traffic, CoreStatsEntry):
// ядро само раз в секунду пишет строку {"up":…,"down":…} — байты за прошедшую секунду. Одно
// соединение на сессию, без опроса списка соединений. Считается всё, что проходит через ядро:
// и через VPN, и правилами «напрямую»; локальная сеть идёт мимо ядра и сюда не попадает.
public sealed class TrafficMeter : IDisposable
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource stop = new();
    private long up, down, total, lastTicks;

    public TrafficMeter(CoreStatsEntry entry) => _ = RunAsync(entry, stop.Token);

    // Байт в секунду за последнюю секунду; 0 — ядро молчит дольше трёх секунд.
    public long Up => Fresh ? Interlocked.Read(ref up) : 0;
    public long Down => Fresh ? Interlocked.Read(ref down) : 0;
    // Байт в обе стороны с начала сессии.
    public long Total => Interlocked.Read(ref total);
    public bool Fresh => DateTime.UtcNow.Ticks - Interlocked.Read(ref lastTicks) < TimeSpan.TicksPerSecond * 3;

    private async Task RunAsync(CoreStatsEntry entry, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{entry.Port}/traffic");
                request.Headers.Authorization = new("Bearer", entry.Secret);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false));
                    while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                        if (Parse(line) is var (u, d)) Record(u, d);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException) { }
            // Ядро перезапускается или ещё не поднялось — повтор без спешки.
            try { await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal void Record(long u, long d)
    {
        Interlocked.Exchange(ref up, u); Interlocked.Exchange(ref down, d);
        Interlocked.Add(ref total, u + d);
        Interlocked.Exchange(ref lastTicks, DateTime.UtcNow.Ticks);
    }

    internal static (long Up, long Down)? Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is JsonObject o && o["up"]?.GetValue<long>() is long u && o["down"]?.GetValue<long>() is long d && u >= 0 && d >= 0)
                return (u, d);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        return null;
    }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    // «2,4 МБ/с», «850 КБ/с», «0 КБ/с».
    public static string Rate(long bytesPerSecond) => Size(bytesPerSecond) + "/с";

    // «1,2 ГБ», «350 МБ», «12 КБ».
    public static string Size(long bytes) =>
        bytes >= 1_000_000_000 ? (bytes / 1e9).ToString(bytes >= 10_000_000_000 ? "0" : "0.#", Ru) + " ГБ"
        : bytes >= 1_000_000 ? (bytes / 1e6).ToString(bytes >= 10_000_000 ? "0" : "0.#", Ru) + " МБ"
        : (bytes / 1000).ToString(Ru) + " КБ";

    public void Dispose() => stop.Cancel();
}

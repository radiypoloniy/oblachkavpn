namespace Oblachka;

// Handshake — это только отклик входа сервера (ServerPing, TCP-рукопожатие), а не задержка
// через сервер. У каскадов вход стоит рядом с человеком, и рукопожатие показывало единицы
// миллисекунд при настоящей задержке в сотни. Такой отклик говорит лишь «сервер отвечает»:
// числом он не показывается и в выборе лучшего не участвует.
public sealed record ServerMeasurement(string Fingerprint, DateTimeOffset Time, bool Success, long Milliseconds)
{
    public bool Handshake { get; init; }
    // Скорость скачивания через сервер, байт в секунду; 0 — не мерили. Меряется реже задержки.
    public long Throughput { get; init; }
}

public static class ServerMeasurements
{
    public const int Parallel = 4;
    // Замер скорости: 256 КБ через сервер. Немного трафика — и уже видно узкий канал.
    public static readonly Uri ThroughputTarget = new("https://speed.cloudflare.com/__down?bytes=262144");
    // Держимся текущего сервера, пока он хуже лучшего не больше чем на столько.
    public const double Stickiness = 1.2;

    // Оценка «как быстро откроется обычная страница»: задержка плюс время скачать 1 МБ.
    // Одной задержки мало: Hysteria2 (QUIC) делает меньше обменов на соединение и всегда
    // выглядит быстрее, даже с узким каналом. Без замера скорости — как у сервера на 2,5 МБ/с.
    public static double Score(ServerMeasurement m) =>
        !m.Success || m.Handshake ? double.MaxValue : m.Milliseconds + 1_000_000_000.0 / (m.Throughput > 0 ? m.Throughput : 2_500_000);

    public static bool Fresh(ServerMeasurement m, DateTimeOffset now) => m.Time <= now && now - m.Time < TimeSpan.FromMinutes(20);

    // Лучший по свежей оценке (Score); отклики рукопожатия не в счёт. current — сервер, на котором
    // уже работаем: он остаётся, пока хуже лучшего не больше чем в Stickiness раз.
    public static int? Best(IReadOnlyList<Profile> profiles, IReadOnlyDictionary<string, ServerMeasurement> measurements, DateTimeOffset now, Profile? current = null)
    {
        double ScoreOf(int i) => measurements.TryGetValue(Profiles.Fingerprint(profiles[i]), out var m) && Fresh(m, now) ? Score(m) : double.MaxValue;
        var ranked = Enumerable.Range(0, profiles.Count).Where(i => ScoreOf(i) < double.MaxValue).OrderBy(ScoreOf).ToArray();
        if (ranked.Length == 0) return null;
        if (current is not null && Enumerable.Range(0, profiles.Count).FirstOrDefault(i => ProfileSelection.Identity(profiles[i]) == ProfileSelection.Identity(current), -1) is var kept and >= 0 &&
            ScoreOf(kept) <= ScoreOf(ranked[0]) * Stickiness) return kept;
        return ranked[0];
    }

    // Настоящий HTTPS-запрос через каждый сервер отдельным ядром, по Parallel одновременно.
    // route — чем подключаться к серверу: под «Весь компьютер» это профиль с адресом, который
    // защита разрешила ядрам; null — сейчас не проверить, сервер пропускается.
    public static async Task MeasureAsync(IReadOnlyList<Profile> profiles, Func<ITunnelController> factory,
        Action<ServerMeasurement> measured, CancellationToken token, Func<Profile, Profile?>? route = null, bool throughput = false)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromMinutes(5));
        var queue = new Queue<Profile>(profiles.Take(Subscriptions.MaxProfiles));
        var gate = new object();
        async Task Worker()
        {
            using var controller = factory();
            while (true)
            {
                Profile profile;
                lock (gate) { if (queue.Count == 0) return; profile = queue.Dequeue(); }
                budget.Token.ThrowIfCancellationRequested();
                if ((route is null ? profile : route(profile)) is not { } target) continue;
                try
                {
                    await controller.StartAsync(target, budget.Token);
                    var round = await new TunnelHealth().CheckAsync(controller.ProbeAsync, TunnelHealth.Targets, budget.Token);
                    var success = round.Results.FirstOrDefault(r => r.Success);
                    var speed = success is not null && throughput ? await ThroughputAsync(controller.Port, budget.Token) : 0;
                    measured(new(Profiles.Fingerprint(profile), DateTimeOffset.UtcNow, success is not null, success?.Milliseconds ?? 0) { Throughput = speed });
                }
                catch (UserError) { measured(new(Profiles.Fingerprint(profile), DateTimeOffset.UtcNow, false, 0)); }
                finally { controller.Stop(); }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(Parallel, Math.Max(1, queue.Count))).Select(_ => Worker()));
    }

    // Скорость через локальный SOCKS ядра: байт в секунду, 0 — не вышло.
    public static async Task<long> ThroughputAsync(int socksPort, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var handler = new System.Net.Http.SocketsHttpHandler { UseProxy = true, Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{socksPort}", false), PooledConnectionLifetime = TimeSpan.Zero };
        using var client = new System.Net.Http.HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        long total = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(ThroughputTarget, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return 0;
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[32768];
            clock.Restart();
            int read;
            while ((read = await body.ReadAsync(buffer, timeout.Token)) > 0) total += read;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException) { return 0; }
        return total < 64 * 1024 ? 0 : (long)(total / Math.Max(clock.Elapsed.TotalSeconds, 0.05));
    }
}

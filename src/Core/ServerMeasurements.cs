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
    // Замер скорости: до ThroughputBytes за ThroughputWindow через сервер. Прежние 256 КБ TCP-протоколы
    // (VLESS, REALITY, XHTTP) проходили в медленном старте — скорость выходила почти одной задержкой,
    // а Hysteria2 (QUIC; с заданной скоростью — Brutal) разгонялась быстрее и выигрывала автовыбор почти всегда. Первые
    // ThroughputWarmup байт — разгон соединения, в скорость не считаются.
    public static readonly Uri ThroughputTarget = new("https://speed.cloudflare.com/__down?bytes=8388608");
    public const long ThroughputBytes = 4 * 1024 * 1024, ThroughputWarmup = 128 * 1024;
    public static readonly TimeSpan ThroughputWindow = TimeSpan.FromSeconds(3);
    // Скорость меряется у стольких лучших по задержке (и у текущего сервера): трафика — как раньше на всех.
    public const int ThroughputCandidates = 3;
    // Без замера скорости — как у сервера на 2,5 МБ/с, если сравнить не с кем.
    public const long DefaultThroughput = 2_500_000;
    // Держимся текущего сервера, пока он хуже лучшего не больше чем на столько.
    // «Для продвинутых»: порог переключения автовыбора; 1.2 — другой сервер лучше на 20 %.
    public static double Stickiness { get; set; } = 1.2;

    // Оценка «как быстро откроется обычная страница»: задержка плюс время скачать 1 МБ.
    // Одной задержки мало: Hysteria2 (QUIC) делает меньше обменов на соединение и всегда
    // выглядит быстрее, даже с узким каналом. Без замера скорости — fallback: самая низкая из
    // измеренных у соседей (FallbackThroughput). Иначе на медленной сети сервер без замера с
    // условными 2,5 МБ/с обгонял бы честно измеренные.
    public static double Score(ServerMeasurement m, long fallback = DefaultThroughput) =>
        !m.Success || m.Handshake ? double.MaxValue : m.Milliseconds + 1_000_000_000.0 / (m.Throughput > 0 ? m.Throughput : fallback);

    public static long FallbackThroughput(IEnumerable<ServerMeasurement> fresh) =>
        fresh.Where(m => m is { Success: true, Handshake: false, Throughput: > 0 }).Select(m => m.Throughput).DefaultIfEmpty(DefaultThroughput).Min();

    // У кого мерить скорость: лучшие по задержке среди ответивших и текущий сервер.
    public static IReadOnlyList<Profile> ThroughputTargets(IReadOnlyList<Profile> profiles, IReadOnlyDictionary<string, ServerMeasurement> measurements, DateTimeOffset now, Profile? current = null)
    {
        var ranked = profiles.Where(p => measurements.TryGetValue(Profiles.Fingerprint(p), out var m) && Fresh(m, now) && m is { Success: true, Handshake: false })
            .OrderBy(p => measurements[Profiles.Fingerprint(p)].Milliseconds).Take(ThroughputCandidates).ToList();
        if (current is not null && profiles.FirstOrDefault(p => ProfileSelection.Identity(p) == ProfileSelection.Identity(current)) is { } kept && !ranked.Contains(kept) &&
            measurements.TryGetValue(Profiles.Fingerprint(kept), out var own) && own.Success) ranked.Add(kept);
        return ranked;
    }

    public static bool Fresh(ServerMeasurement m, DateTimeOffset now) => m.Time <= now && now - m.Time < TimeSpan.FromMinutes(20);

    // Лучший по свежей оценке (Score); отклики рукопожатия не в счёт. current — сервер, на котором
    // уже работаем: он остаётся, пока хуже лучшего не больше чем в Stickiness раз.
    public static int? Best(IReadOnlyList<Profile> profiles, IReadOnlyDictionary<string, ServerMeasurement> measurements, DateTimeOffset now, Profile? current = null)
    {
        var fallback = FallbackThroughput(profiles.Select(p => measurements.GetValueOrDefault(Profiles.Fingerprint(p))).OfType<ServerMeasurement>().Where(m => Fresh(m, now)));
        double ScoreOf(int i) => measurements.TryGetValue(Profiles.Fingerprint(profiles[i]), out var m) && Fresh(m, now) ? Score(m, fallback) : double.MaxValue;
        var ranked = Enumerable.Range(0, profiles.Count).Where(i => ScoreOf(i) < double.MaxValue).OrderBy(ScoreOf).ToArray();
        if (ranked.Length == 0) return null;
        if (current is not null && Enumerable.Range(0, profiles.Count).FirstOrDefault(i => ProfileSelection.Identity(profiles[i]) == ProfileSelection.Identity(current), -1) is var kept and >= 0 &&
            ScoreOf(kept) <= ScoreOf(ranked[0]) * Stickiness) return kept;
        return ranked[0];
    }

    // Настоящий HTTPS-запрос через каждый сервер отдельным ядром, по Parallel одновременно (скорость — по одному).
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
        // Скорость — по одному серверу: одновременные загрузки делят канал, и Hysteria2 (QUIC, а с
        // заданной скоростью — Brutal) уступает полосу хуже TCP-соседей — замер мерил толкотню, а не сервер.
        await Task.WhenAll(Enumerable.Range(0, Math.Min(throughput ? 1 : Parallel, Math.Max(1, queue.Count))).Select(_ => Worker()));
    }

    // Скорость через локальный SOCKS ядра: байт в секунду, 0 — не вышло.
    public static async Task<long> ThroughputAsync(int socksPort, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var window = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var handler = new System.Net.Http.SocketsHttpHandler { UseProxy = true, Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{socksPort}", false), PooledConnectionLifetime = TimeSpan.Zero };
        using var client = new System.Net.Http.HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        long total = 0, warm = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(ThroughputTarget, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return 0;
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[65536];
            int read;
            while (total < ThroughputBytes && (read = await body.ReadAsync(buffer, window.Token)) > 0)
            {
                total += read;
                // Разгон позади — отсюда считаем скорость, и с этого момента идёт окно замера.
                if (warm == 0 && total >= ThroughputWarmup) { warm = total; clock.Restart(); window.CancelAfter(ThroughputWindow); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException) { return 0; }
        // Не дошли до конца разгона — канал совсем узкий: скорость по всему, что пришло. После разгона
        // скорость настоящая, даже если поток замер: 0 значил бы «не мерили» и подставил бы чужую.
        return warm == 0 ? (total < 64 * 1024 ? 0 : (long)(total / Math.Max(clock.Elapsed.TotalSeconds, 0.05)))
            : Math.Max(1, (long)((total - warm) / Math.Max(clock.Elapsed.TotalSeconds, 0.05)));
    }
}

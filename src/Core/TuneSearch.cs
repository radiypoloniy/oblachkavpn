namespace Oblachka;

// «Подобрать лучшее»: у лучших по замерам серверов пробуются поправки (ServerTweak) — каждая
// отдельным ядром, настоящей загрузкой. Три шага:
//   отсев — каждый вариант по одному замеру;
//   подтверждение — два лучших варианта и исходный вариант их сервера ещё по Confirm замеров:
//     один удачный замер — ещё не стабильность, сеть шумит;
//   выбор — поправка остаётся, только если лучше исходного варианта своего сервера с запасом
//     (ServerMeasurements.Stickiness), иначе сервер берётся как в подписке.
// Оценка — как у автовыбора (Score): задержка плюс время скачать 1 МБ. Хоть один неудачный замер
// варианта — вариант выбывает: стабильность важнее пиковой скорости.
public sealed record TuneTrial(Profile Server, ServerTweak Tweak, ServerMeasurement Result);
public sealed record TuneResult(Profile Server, ServerTweak Tweak, ServerMeasurement Before, ServerMeasurement After);

public static class TuneSearch
{
    public const int Servers = 2, Confirm = 2, Finalists = 2;

    public delegate Task<ServerMeasurement> Measure(Profile server, ServerTweak tweak, CancellationToken token);

    public static int Steps(IReadOnlyList<Profile> ranked) =>
        ranked.Take(Servers).Sum(p => ServerTweak.Candidates(p).Count) + (Finalists + 1) * Confirm;

    // ranked — серверы от лучшего по нынешним замерам. null — ни один вариант не ответил.
    public static async Task<TuneResult?> RunAsync(IReadOnlyList<Profile> ranked, Measure measure, Action<TuneTrial>? progress, CancellationToken token)
    {
        var runs = new Dictionary<(Profile, ServerTweak), List<ServerMeasurement>>();
        async Task Run((Profile Server, ServerTweak Tweak) variant)
        {
            var result = await measure(variant.Server, variant.Tweak, token);
            if (!runs.TryGetValue(variant, out var list)) runs[variant] = list = [];
            list.Add(result);
            progress?.Invoke(new(variant.Server, variant.Tweak, result));
        }
        foreach (var server in ranked.Take(Servers))
            foreach (var tweak in ServerTweak.Candidates(server))
                await Run((server, tweak));

        var finalists = runs.Where(r => r.Value[0].Success).OrderBy(r => Score(r.Value)).Take(Finalists).Select(r => r.Key).ToList();
        if (finalists.Count == 0) return null;
        var home = (finalists[0].Item1, new ServerTweak());
        if (!finalists.Contains(home)) finalists.Add(home);
        foreach (var variant in finalists)
            for (var i = 0; i < Confirm; i++) await Run(variant);

        var best = finalists.OrderBy(v => Score(runs[v])).First();
        var plain = (best.Item1, new ServerTweak());
        if (!best.Item2.IsEmpty && runs.TryGetValue(plain, out var plainRuns) && Score(runs[best]) * ServerMeasurements.Stickiness > Score(plainRuns)) best = plain;
        if (Score(runs[best]) == double.MaxValue) return null;
        var before = runs.TryGetValue((ranked[0], new ServerTweak()), out var start) ? Combine(start) : Combine(runs[best]);
        return new(best.Item1, best.Item2, before, Combine(runs[best]));
    }

    // Замеры одного варианта вместе: медиана задержки и скорости; хоть один неудачный — неудача.
    public static ServerMeasurement Combine(IReadOnlyList<ServerMeasurement> list)
    {
        var last = list[^1];
        if (list.Any(m => !m.Success)) return last with { Success = false };
        static long Median(IEnumerable<long> values) { var sorted = values.OrderBy(v => v).ToArray(); return sorted[sorted.Length / 2]; }
        return last with { Milliseconds = Median(list.Select(m => m.Milliseconds)), Throughput = Median(list.Select(m => m.Throughput)) };
    }

    private static double Score(IReadOnlyList<ServerMeasurement> list) => ServerMeasurements.Score(Combine(list));
}

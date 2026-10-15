namespace Oblachka;

// Handshake — это только отклик входа сервера (ServerPing, TCP-рукопожатие), а не задержка
// через сервер. У каскадов вход стоит рядом с человеком, и рукопожатие показывало единицы
// миллисекунд при настоящей задержке в сотни. Такой отклик говорит лишь «сервер отвечает»:
// числом он не показывается и в выборе лучшего не участвует.
public sealed record ServerMeasurement(string Fingerprint, DateTimeOffset Time, bool Success, long Milliseconds)
{
    public bool Handshake { get; init; }
}

public static class ServerMeasurements
{
    public const int Parallel = 4;

    public static bool Fresh(ServerMeasurement m, DateTimeOffset now) => m.Time <= now && now - m.Time < TimeSpan.FromMinutes(20);

    // Лучший по свежей задержке через сервер; отклики рукопожатия не в счёт.
    public static int? Best(IReadOnlyList<Profile> profiles, IReadOnlyDictionary<string, ServerMeasurement> measurements, DateTimeOffset now) =>
        profiles.Select((p, i) => (key: Profiles.Fingerprint(p), i)).Where(x => measurements.TryGetValue(x.key, out var m) &&
            m.Success && !m.Handshake && Fresh(m, now))
            .OrderBy(x => measurements[x.key].Milliseconds).Select(x => (int?)x.i).FirstOrDefault();

    // Настоящий HTTPS-запрос через каждый сервер отдельным ядром, по Parallel одновременно.
    // route — чем подключаться к серверу: под «Весь компьютер» это профиль с адресом, который
    // защита разрешила ядрам; null — сейчас не проверить, сервер пропускается.
    public static async Task MeasureAsync(IReadOnlyList<Profile> profiles, Func<ITunnelController> factory,
        Action<ServerMeasurement> measured, CancellationToken token, Func<Profile, Profile?>? route = null)
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
                    measured(new(Profiles.Fingerprint(profile), DateTimeOffset.UtcNow, success is not null, success?.Milliseconds ?? 0));
                }
                catch (UserError) { measured(new(Profiles.Fingerprint(profile), DateTimeOffset.UtcNow, false, 0)); }
                finally { controller.Stop(); }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(Parallel, Math.Max(1, queue.Count))).Select(_ => Worker()));
    }
}

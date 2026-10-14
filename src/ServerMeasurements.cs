namespace Oblachka;

public sealed record ServerMeasurement(string Fingerprint, DateTimeOffset Time, bool Success, long Milliseconds);

public static class ServerMeasurements
{
    public static bool Fresh(ServerMeasurement m, DateTimeOffset now) => m.Time <= now && now - m.Time < TimeSpan.FromMinutes(20);

    public static int? Best(IReadOnlyList<Profile> profiles, IReadOnlyDictionary<string, ServerMeasurement> measurements, DateTimeOffset now) =>
        profiles.Select((p, i) => (key: Profiles.Fingerprint(p), i)).Where(x => measurements.TryGetValue(x.key, out var m) &&
            m.Success && Fresh(m, now))
            .OrderBy(x => measurements[x.key].Milliseconds).Select(x => (int?)x.i).FirstOrDefault();

    public static async Task MeasureAsync(IReadOnlyList<Profile> profiles, Func<ITunnelController> factory,
        Action<ServerMeasurement> measured, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromMinutes(5));
        using var controller = factory();
        foreach (var profile in profiles.Take(Subscriptions.MaxProfiles))
        {
            budget.Token.ThrowIfCancellationRequested();
            try
            {
                await controller.StartAsync(profile, budget.Token);
                var round = await new TunnelHealth().CheckAsync(controller.ProbeAsync, TunnelHealth.Targets, budget.Token);
                var success = round.Results.FirstOrDefault(r => r.Success);
                measured(new(Profiles.Fingerprint(profile), DateTimeOffset.UtcNow, success is not null, success?.Milliseconds ?? 0));
            }
            catch (UserError) { measured(new(Profiles.Fingerprint(profile), DateTimeOffset.UtcNow, false, 0)); }
            finally { controller.Stop(); }
        }
    }
}

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientMeasurementChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var a = Profiles.Parse(link); var b = Profiles.Parse(link.Replace("example.com", "second.invalid").Replace("#Test", "#Second"));
        await check("Background measurement retains speed between latency rounds and measures throughput only every three hours", async () =>
        {
            var now = DateTimeOffset.UtcNow; var backend = new MeasurementBackend();
            using var owner = MeasurementOwner(backend, a, b, () => now); owner.Measurements.Enabled = true;
            await owner.Measurements.PollAsync(); Assert(backend.Rounds.Count(r => r.Speed) == 1);
            now = now.AddMinutes(11); await owner.Measurements.PollAsync();
            Assert(backend.Rounds.Count(r => r.Speed) == 1 && owner.Profiles.Measurements.Values.All(m => m.Throughput == 5000));
            now = now.AddHours(4); await owner.Measurements.PollAsync(); Assert(backend.Rounds.Count(r => r.Speed) == 2);
        });
        await check("Metered networks skip automatic downloads while an explicit speed test remains available", async () =>
        {
            var backend = new MeasurementBackend { IsMetered = true };
            using var owner = MeasurementOwner(backend, a); owner.Measurements.Enabled = true;
            await owner.Measurements.PollAsync(); Assert(backend.Rounds.All(r => !r.Speed));
            await owner.Measurements.MeasureAsync([a], false); Assert(backend.Rounds.Any(r => r.Speed));
        });
        await check("A cancelled measurement rejects late results even if its backend ignores cancellation", async () =>
        {
            var backend = new MeasurementBackend { Gate = new(), IgnoreCancel = true };
            using var owner = MeasurementOwner(backend, a);
            var running = owner.Measurements.MeasureAsync([a], false); Assert(owner.Measurements.IsMeasuring);
            var duplicate = owner.Measurements.MeasureAsync([b], false); await duplicate;
            owner.Measurements.CancelMeasure(); backend.Gate.SetResult(); await running;
            Assert(owner.Profiles.Measurements.Count == 0 && backend.Rounds.Count == 1 && !owner.Measurements.IsMeasuring);
        });
        await check("A network transition invalidates pending pings and forces a fresh latency round", async () =>
        {
            var backend = new MeasurementBackend { PingGate = new() };
            using var owner = MeasurementOwner(backend, a); owner.Measurements.Enabled = true;
            owner.Measurements.QueuePing(); var ping = owner.Measurements.PingTask;
            owner.Measurements.InvalidateNetwork(); backend.PingGate.SetResult(); await ping;
            Assert(owner.Profiles.Pings.Count == 0);
            backend.PingGate = null; await owner.Measurements.MeasureAsync([a], true);
            owner.Measurements.InvalidateNetwork(); Assert(owner.Measurements.LastMeasure == DateTimeOffset.MinValue);
        });
        await check("TUN measurement uses physical pinned routes and skips nodes outside the protected lease", async () =>
        {
            var backend = new MeasurementBackend(); var core = new RecoveryCore { Working = a };
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), measurementBackend: backend);
            owner.Profiles.Imported = [a, b]; owner.Profiles.GroupProfiles["fixture"] = [a, b]; owner.Preferences.SystemTunnel = true;
            await owner.Connections.StartAsync(a); var session = owner.Tunnel;
            await owner.Measurements.MeasureAsync([a, b], true);
            Assert(backend.Rounds.All(r => r.Physical) && backend.Measured.All(p => Profiles.Fingerprint(p) == Profiles.Fingerprint(a)));
            Assert(ReferenceEquals(session, owner.Tunnel) && owner.Tunnel?.BlocksBypass == true);
            await owner.Connections.StopAsync();
        });
        await check("Background pool excludes free nodes and manual exclusions, and a busy owner postpones it", async () =>
        {
            var backend = new MeasurementBackend { IsMetered = true }; var free = b with { Source = ProfileSource.Free };
            using var owner = MeasurementOwner(backend, a, b, free); owner.Measurements.Enabled = true;
            owner.Profiles.AutoPool.Set(b, false); using var busy = new CancellationTokenSource(); owner.Activity.Operation = busy;
            await owner.Measurements.PollAsync(); Assert(backend.Rounds.Count == 0);
            owner.Activity.Operation = null; await owner.Measurements.PollAsync();
            Assert(backend.Measured.SequenceEqual(new[] { a }));
        });
        await check("Disposal cancels measurement without publishing late samples or retaining an active task", async () =>
        {
            var backend = new MeasurementBackend { Gate = new(), IgnoreCancel = true }; var owner = MeasurementOwner(backend, a);
            var changes = 0; owner.Measurements.Changed += () => changes++;
            var pending = owner.Measurements.MeasureAsync([a], true); owner.Dispose(); var before = changes;
            backend.Gate.SetResult(); await pending;
            Assert(changes == before && owner.Profiles.Measurements.Count == 0 && !owner.Measurements.IsMeasuring);
        });
    }
    private static ClientRuntime MeasurementOwner(MeasurementBackend backend, params Profile[] profiles) => MeasurementOwner(backend, profiles, null);
    private static ClientRuntime MeasurementOwner(MeasurementBackend backend, Profile a, Profile b, Func<DateTimeOffset> now) => MeasurementOwner(backend, [a, b], now);
    private static ClientRuntime MeasurementOwner(MeasurementBackend backend, Profile[] profiles, Func<DateTimeOffset>? now)
    {
        var owner = new ClientRuntime(new ConnectionCore(), new TunnelHostFixture(), measurementBackend: backend, measurementNow: now);
        backend.Now = now ?? (() => DateTimeOffset.UtcNow);
        owner.Profiles.Imported = profiles; owner.Profiles.Selected = profiles.FirstOrDefault(); owner.Profiles.GroupProfiles["fixture"] = profiles;
        return owner;
    }
    private sealed class MeasurementBackend : IClientMeasurementBackend
    {
        public bool IsMetered, IgnoreCancel;
        public TaskCompletionSource? Gate, PingGate;
        public List<(bool Speed, bool Physical)> Rounds { get; } = new();
        public List<Profile> Measured { get; } = new();
        public Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;
        public string? Network() => "fixture-network";
        public bool Metered() => IsMetered;
        public async Task PingAsync(IReadOnlyList<Profile> targets, Action<Profile, long?> result, CancellationToken token)
        { if (PingGate is not null) await PingGate.Task; foreach (var p in targets) result(p, 1); }
        public async Task MeasureAsync(IReadOnlyList<Profile> targets, bool physical, Action<ServerMeasurement> result,
            Func<Profile, Profile?> route, bool throughput, CancellationToken token)
        {
            Rounds.Add((throughput, physical)); if (Gate is not null) await Gate.Task;
            if (!IgnoreCancel) token.ThrowIfCancellationRequested();
            foreach (var p in targets)
                if (route(p) is { } routed)
                { Measured.Add(p); result(new(Profiles.Fingerprint(p), Now(), true, 10) { Throughput = throughput ? 5000 : 0 }); }
        }
    }
}

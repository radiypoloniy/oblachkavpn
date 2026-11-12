namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientFeaturePolicyChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var a = Profiles.Parse(link); var b = Profiles.Parse(link.Replace("example.com", "other.invalid")) with { Source = ProfileSource.Free };
        await check("Default policy prevents manual and scheduled server probes while VPN is running", async () =>
        {
            var backend = new MeasurementBackend(); using var owner = MeasurementOwner(backend, a);
            owner.Measurements.Enabled = true;
            await owner.Connections.StartAsync(a);
            await owner.Measurements.MeasureAsync([a], false);
            await owner.Measurements.PollAsync();
            Assert(backend.Rounds.Count == 0 && !owner.Measurements.CanMeasure && owner.Measurements.NextDelay() == Timeout.InfiniteTimeSpan);
            owner.Workspace.Measurements = new() { AllowConnected = true, Parallelism = 2 };
            owner.Measurements.PolicyChanged(); await owner.Measurements.MeasureAsync([a], false);
            Assert(backend.Rounds.Count == 1 && backend.Parallelism.Single() == 2);
        });
        await check("Manual-only measurement settings survive snapshots and disable automatic work", async () =>
        {
            var backend = new MeasurementBackend(); using var owner = MeasurementOwner(backend, a);
            owner.Measurements.Enabled = true;
            owner.Workspace.Measurements = new() { ManualOnly = true, IntervalMinutes = 30, Parallelism = 1 };
            var saved = ClientSnapshotBuilder.Capture(owner);
            Assert(saved.MeasurementOptions == owner.Workspace.Measurements);
            owner.Measurements.QueuePing(); await owner.Measurements.PollAsync();
            Assert(backend.Rounds.Count == 0 && owner.Profiles.Pings.Count == 0 && owner.Measurements.NextDelay() == Timeout.InfiniteTimeSpan);
            await owner.Measurements.MeasureAsync([a], false);
            Assert(backend.Rounds.Count == 1 && backend.Parallelism.Single() == 1);
            Assert(new MeasurementOptions { IntervalMinutes = -1, Parallelism = 999 }.Normalize() == new MeasurementOptions());
        });
        await check("Legacy traffic preserves observed route deltas without guessing a SOCKS exit", () => Sync(() =>
        {
            var traffic = new ProgramTrafficHistory(); var history = new TrafficHistory(); var now = DateTime.Now;
            CoreConnection Row(string id, string tag, long up, long down) => new("browser", false, null, "192.0.2.1", 443, [tag], id, up, down);
            traffic.Record([Row("vpn", "direct-0", 10, 20), Row("direct", "user-direct", 30, 40), Row("bridge", "local-core", 50, 60)], history, now);
            traffic.Record([Row("vpn", "direct-0", 20, 40), Row("direct", "user-direct", 60, 80), Row("bridge", "local-core", 100, 120)], history, now);
            var row = history.Report(now, 1).Programs.Single();
            Assert(row.Total == 420 && row.Routes is { Vpn: 60, Direct: 140 });
            Assert(TrafficRouteSample.From(row.Total, row.Routes) == new TrafficRouteSample(60, 140, 220));
            Assert((Row("external", "direct-0", 1, 1) with { Inbound = "bridge" }).RouteBytes(1, 1) == default);
            Assert(Row("invalid", "direct-00", 1, 1).RouteBytes(1, 1) == default);
        }));
        await check("A compact API delayed at startup is retried before selecting the legacy collector", async () =>
        {
            var calls = 0; var legacy = 0;
            var sampler = new CoreProgramSampler((_, _) => { legacy++; return Task.FromResult<IReadOnlyList<CoreConnection>?>([]); },
                (_, _) => Task.FromResult<CoreProgramSnapshot?>(++calls < 3 ? null : new(Guid.NewGuid().ToString(), [])));
            var stats = CoreStatsEntry.Create(23000);
            Assert(await sampler.ReadAsync(stats, CancellationToken.None) is null);
            Assert(await sampler.ReadAsync(stats, CancellationToken.None) is null);
            Assert((await sampler.ReadAsync(stats, CancellationToken.None))?.Summary is not null && legacy == 0 && !sampler.UsesLegacy);
        });
        await check("A failed replacement retains the handover guard until explicit Stop", async () =>
        {
            var core = new RecoveryCore { Working = a }; var host = new TunnelHostFixture();
            using var owner = new ClientRuntime(core, host); owner.Preferences.SystemTunnel = true;
            owner.Profiles.Imported = [a]; await owner.Connections.StartAsync(a);
            owner.Profiles.Imported = [a, b]; owner.Workspace.FreeConsent = FreeServers.ConsentVersion;
            host.FailAcquire = true;
            await ExpectUserErrorAsync(() => owner.Connections.SelectAsync(b));
            Assert(host.Handover && owner.Tunnels.HandoverPending && owner.Tunnel is null);
            await owner.Connections.StopAsync();
            Assert(!host.Handover && !owner.Tunnels.HandoverPending && !owner.Workspace.WasConnected);
        });
        await check("An invalid replacement is prepared before releasing the existing protected tunnel", async () =>
        {
            var core = new RecoveryCore { Working = a }; var host = new TunnelHostFixture();
            using var owner = new ClientRuntime(core, host); owner.Preferences.SystemTunnel = true;
            owner.Profiles.Imported = [a]; await owner.Connections.StartAsync(a); var session = owner.Tunnel;
            owner.Profiles.Imported = [a, b]; owner.Workspace.FreeConsent = FreeServers.ConsentVersion;
            host.Preparing = _ => Task.FromException(new UserError("invalid candidate"));
            await ExpectUserErrorAsync(() => owner.Connections.SelectAsync(b));
            Assert(ReferenceEquals(session, owner.Tunnel) && session?.BlocksBypass == true && !host.Calls.Contains("release") && !host.Handover);
            await owner.Connections.StopAsync();
        });
        await check("An older helper is detected before changing protection or releasing the active lease", async () =>
        {
            var core = new RecoveryCore { Working = a }; var host = new TunnelHostFixture { SupportsHandover = false };
            using var owner = new ClientRuntime(core, host); owner.Preferences.SystemTunnel = true;
            owner.Profiles.Imported = [a]; await owner.Connections.StartAsync(a); var session = owner.Tunnel;
            owner.Profiles.Imported = [a, b]; owner.Workspace.FreeConsent = FreeServers.ConsentVersion;
            await ExpectUserErrorAsync(() => owner.Connections.SelectAsync(b));
            Assert(ReferenceEquals(session, owner.Tunnel) && session?.BlocksBypass == true && !owner.Tunnels.HandoverPending &&
                !host.Calls.Contains("release") && !host.Calls.Contains("guard-on"));
            await owner.Connections.StopAsync();
        });
        await check("Handover WFP objects are persistent, independent and removable without changing Lockdown", () => Sync(() =>
        {
            using var engine = new FakeWfp { Weight = WfpLockdown.StrictSublayerWeight };
            WfpHandover.Apply(engine);
            Assert(engine.SublayerExists(WfpHandover.Sublayer) && WfpHandover.Filters().All(f => f.Persistent && engine.FilterExists(f.Key)));
            Assert(WfpHandover.Filters().Where(f => f.Permit).All(f => f.Conditions.All(c => c.Kind != WfpConditionKind.Application || c.Program!.EndsWith("Oblachka.exe", StringComparison.Ordinal))));
            WfpHandover.Remove(engine);
            Assert(!engine.SublayerExists(WfpHandover.Sublayer) && engine.Filters.Count == 0);
            Assert(WfpHandover.Filters().All(f => !Enumerable.Range(0, WfpLockdown.MaxFilters).Select(WfpLockdown.FilterKey).Contains(f.Key)));
            HelperProtocol.Validate(new(1, Guid.NewGuid(), HelperOperation.Handover, null, Lockdown: new(true, false)));
            ExpectUserError(() => HelperProtocol.Validate(new(1, Guid.NewGuid(), HelperOperation.Handover, null, Lockdown: new(true, true))));
        }));
    }
}

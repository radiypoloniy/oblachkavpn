using System.Net;

namespace Oblachka;

public static partial class SelfTests
{
    // Сбой ядра в системном режиме — не команда «вернуть обычный интернет». Раньше любое
    // падение ядра снимало сессию у помощника, и блокировка исчезала ровно при обрыве.
    private static async Task SystemHoldChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var first = Profiles.Parse(link);
        var spare = Profiles.Parse(link.Replace("@example.com:443", "@spare.example:443").Replace("#Test", "#Spare"));
        TunnelEndpointPlan Plan(Profile p, string peer) => new(p, new[] { peer }) { Source = p };

        await check("Core failure in system mode holds the tunnel and the block instead of releasing them", async () =>
        {
            var calls = new List<string>(); var core = new HoldCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartAsync(Plan(first, "192.0.2.1"), CancellationToken.None);
            await FailAndWaitAsync(session, core);
            Assert(session.State == SystemTunnelState.Holding && session.BlocksBypass);
            Assert(!calls.Contains("helper:release"));
        });
        await check("Restart after a core failure reuses the lease and the tunnel's port", async () =>
        {
            var calls = new List<string>(); var core = new HoldCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartAsync(Plan(first, "192.0.2.1"), CancellationToken.None);
            await FailAndWaitAsync(session, core);
            await session.SwitchAsync(null, CancellationToken.None);
            Assert(session.State == SystemTunnelState.Running && core.Started.Count == 2);
            Assert(calls.Count(c => c == "helper:acquire") == 1 && !calls.Contains("helper:release"));
        });
        await check("A failed restart keeps holding, and only an explicit stop releases the network", async () =>
        {
            var calls = new List<string>(); var core = new HoldCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartAsync(Plan(first, "192.0.2.1"), CancellationToken.None);
            core.RefuseStart = true;
            await ExpectUserErrorAsync(() => session.SwitchAsync(null, CancellationToken.None));
            Assert(session.State == SystemTunnelState.Holding && session.BlocksBypass && !calls.Contains("helper:release"));
            await session.StopAsync();
            Assert(session.State == SystemTunnelState.Idle && calls.Contains("helper:release") && core.ProcessId is null);
        });
        await check("A core that cannot take the tunnel's port again is stopped, not left carrying nothing", async () =>
        {
            var calls = new List<string>(); var core = new HoldCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartAsync(Plan(first, "192.0.2.1"), CancellationToken.None);
            core.NextPort = 20001;
            await ExpectUserErrorAsync(() => session.SwitchAsync(null, CancellationToken.None));
            Assert(session.State == SystemTunnelState.Holding && core.ProcessId is null);
        });
        await check("Spare servers are allowed at start, and switching between them keeps the block", async () =>
        {
            var calls = new List<string>(); var core = new HoldCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartWithSparesAsync(new[] { Plan(first, "192.0.2.1"), Plan(spare, "192.0.2.2") }, CancellationToken.None);
            Assert(helper.LastPeers.SequenceEqual(new[] { "192.0.2.1", "192.0.2.2" }));
            Assert(ReferenceEquals(core.Started[0], first) && session.CanSwitchTo(spare));
            await session.SwitchAsync(spare, CancellationToken.None);
            Assert(ReferenceEquals(core.Started[^1], spare) && session.State == SystemTunnelState.Running);
            // Узел, адрес которого не разрешён, без снятия блокировки недостижим: отказ, а не подмена.
            var stranger = Profiles.Parse(link.Replace("@example.com:443", "@other.example:443").Replace("#Test", "#Other"));
            Assert(!session.CanSwitchTo(stranger));
            await ExpectUserErrorAsync(() => session.SwitchAsync(stranger, CancellationToken.None));
            Assert(session.State == SystemTunnelState.Running && ReferenceEquals(core.Started[^1], spare));
            Assert(!calls.Contains("helper:release"));
        });
        await check("Route search under the block switches through the session and never stops the core itself", async () =>
        {
            var calls = new List<string>(); var core = new HoldCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartWithSparesAsync(new[] { Plan(first, "192.0.2.1"), Plan(spare, "192.0.2.2") }, CancellationToken.None);
            core.Reachable = p => ReferenceEquals(p, spare);
            var result = await new RouteSearch(session.SwitchAsync, core.ProbeAsync, () => { }).FindAsync(
                new[] { first, spare }, 0, new Uri("https://example.com/"), CancellationToken.None);
            Assert(result.ProfileIndex == 1 && session.State == SystemTunnelState.Running && !calls.Contains("helper:release"));
        });
        await check("Losing the helper while holding reports Failed: the helper restores the network itself", async () =>
        {
            var calls = new List<string>(); var core = new HoldCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartAsync(Plan(first, "192.0.2.1"), CancellationToken.None);
            await FailAndWaitAsync(session, core);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Changed += state => { if (state == SystemTunnelState.Failed) failed.TrySetResult(); };
            helper.Lose(); await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(!session.BlocksBypass && core.ProcessId is null);
        });
        await check("Session steps are traced with time and kind only, apply before restore", () =>
        {
            var steps = new List<string>(); var request = NetworkAcquire();
            var session = new NetworkSession(new MemoryNetworkJournal(), new FakeNetworkBackend(), steps.Add);
            session.Acquire(request); session.Release(request.LeaseId);
            Assert(steps.Count >= 2 && steps[0].Contains(" apply ") && steps[^1].Contains(" restore "));
            Assert(steps.All(s => System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d\d:\d\d:\d\d\.\d{3} (apply|restore) \w+ \w+$")));
            Assert(steps.All(s => !s.Contains("192.0.2", StringComparison.Ordinal) && !s.Contains("127.0.0.1", StringComparison.Ordinal)));
            return Task.CompletedTask;
        });
        await check("Spare plans: the selected server must prepare, spares that fail or overflow are skipped", async () =>
        {
            var many = Enumerable.Range(1, 9).Select(i => IPAddress.Parse("198.51.100." + i)).ToArray();
            Task<IPAddress[]> Resolve(string host, CancellationToken _) => host switch
            {
                "example.com" => Task.FromResult(many.Take(9).ToArray()),
                "spare.example" => Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException(11001)),
                _ => Task.FromResult(Enumerable.Range(1, 8).Select(i => IPAddress.Parse("203.0.113." + i)).ToArray())
            };
            var other = Profiles.Parse(link.Replace("@example.com:443", "@other.example:443").Replace("#Test", "#Other"));
            var plans = await TunnelEndpoints.PrepareSetAsync(new[] { first, spare, other }, CancellationToken.None, Resolve);
            // 9 + 8 адресов не помещаются в лимит помощника, недоступный запасной пропущен.
            Assert(plans.Count == 1 && ReferenceEquals(plans[0].Source, first));
            await ExpectUserErrorAsync(() => TunnelEndpoints.PrepareSetAsync(new[] { first }, CancellationToken.None,
                (_, _) => Task.FromResult(new[] { IPAddress.Loopback })));
            Assert(RouteSearch.Order(4, 2).SequenceEqual(new[] { 2, 0, 1, 3 }) && RouteSearch.Order(0, 0).Length == 0);
        });
    }

    private static async Task FailAndWaitAsync(SystemTunnelSession session, HoldCore core)
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += state => { if (state == SystemTunnelState.Holding) holding.TrySetResult(); };
        core.Fail();
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private sealed class HoldCore(List<string> calls) : ITunnelController
    {
        public readonly List<Profile> Started = new();
        public bool RefuseStart;
        public int NextPort = 10808;
        public Func<Profile, bool> Reachable = _ => true;
        public int Port { get; private set; }
        public int? ProcessId { get; private set; }
        public ConnectionState State { get; private set; }
        public string CoreLabel => "fixture";
        public event Action<ConnectionState, string>? Changed;
        public Task StartAsync(Profile profile, CancellationToken token)
        {
            calls.Add("core:start");
            if (RefuseStart) { State = ConnectionState.Failed; Changed?.Invoke(State, "fixture"); throw new UserError("fixture"); }
            Started.Add(profile); Port = NextPort; ProcessId = 42; State = ConnectionState.Running;
            Changed?.Invoke(State, "fixture");
            return Task.CompletedTask;
        }
        public void Fail() { ProcessId = null; State = ConnectionState.Failed; Changed?.Invoke(State, "fixture"); }
        public Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token) => Task.FromResult(
            Started.Count > 0 && Reachable(Started[^1]) ? new ProbeResult(true, 1, "fixture") :
            new ProbeResult(false, 1, "fixture", ProbeFailure.Reachability));
        public void Stop() { calls.Add("core:stop"); ProcessId = null; Port = 0; State = ConnectionState.Idle; }
        public void Dispose() => Stop();
    }
}

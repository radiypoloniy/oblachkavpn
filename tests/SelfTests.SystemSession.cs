using System.Net;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task SystemSessionChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Endpoint pinning preserves TLS name and original profile while bounding peer exclusions", async () =>
        {
            var original = Profiles.Parse(link); var before = original.Outbound.ToJsonString();
            var plan = await TunnelEndpoints.PrepareAsync(original, CancellationToken.None,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("2001:db8::1"), IPAddress.Parse("192.0.2.1") }));
            Assert(plan.Peers.SequenceEqual(new[] { "192.0.2.1", "2001:db8::1" }));
            Assert(plan.PinnedProfile.Outbound["settings"]!["vnext"]![0]!["address"]!.GetValue<string>() == "192.0.2.1");
            Assert(plan.PinnedProfile.Outbound["streamSettings"]!["tlsSettings"]!["serverName"]!.GetValue<string>() == "example.com");
            Assert(original.Outbound.ToJsonString() == before);
            await ExpectUserErrorAsync(() => TunnelEndpoints.PrepareAsync(original, CancellationToken.None,
                (_, _) => Task.FromResult(new[] { IPAddress.Loopback })));
        });
        await check("System session starts core before helper and restores network before stopping core", async () =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartAsync(new(Profiles.Parse(link), new[] { "192.0.2.1" }), CancellationToken.None);
            Assert(session.State == SystemTunnelState.Running);
            session.Changed += state => { if (state == SystemTunnelState.Idle) Assert(core.ProcessId is null); };
            await session.StopAsync();
            Assert(calls.Take(4).SequenceEqual(new[] { "core:start", "helper:acquire", "helper:release", "core:stop" }));
            Assert(session.State == SystemTunnelState.Idle);
        });
        await check("Unconfirmed cleanup remains RecoveryRequired and still stops the core", async () =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls); var helper = new SessionHelper(calls) { ReleaseState = NetworkSessionState.RecoveryRequired };
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartAsync(new(Profiles.Parse(link), new[] { "192.0.2.1" }), CancellationToken.None);
            await session.StopAsync(); Assert(session.State == SystemTunnelState.RecoveryRequired && core.ProcessId is null);
            helper.ReleaseState = NetworkSessionState.Released; await session.StopAsync(); Assert(session.State == SystemTunnelState.Idle);
        });
        await check("Helper acquisition failure triggers independent cleanup and no false Running state", async () =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls); var helper = new SessionHelper(calls) { FailAcquire = true };
            await using var session = new SystemTunnelSession(core, helper);
            await ExpectUserErrorAsync(() => session.StartAsync(new(Profiles.Parse(link), new[] { "192.0.2.1" }), CancellationToken.None));
            Assert(session.State == SystemTunnelState.Idle && core.ProcessId is null && calls.Contains("helper:release"));
        });
        await check("Stop during helper acquisition lets it finish, then releases: no orphaned session", async () =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls); var helper = new SessionHelper(calls);
            using var stop = new CancellationTokenSource();
            helper.DuringAcquire = stop.Cancel;
            await using var session = new SystemTunnelSession(core, helper);
            try { await session.StartAsync(new(Profiles.Parse(link), new[] { "192.0.2.1" }), stop.Token); throw new InvalidOperationException("Start was not cancelled"); }
            catch (OperationCanceledException) { }
            Assert(session.State == SystemTunnelState.Idle && core.ProcessId is null);
            Assert(calls.IndexOf("helper:release") > calls.IndexOf("helper:acquire"));
        });
        await check("Lost helper connection triggers asynchronous cleanup outside the event callback", async () =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper);
            await session.StartAsync(new(Profiles.Parse(link), new[] { "192.0.2.1" }), CancellationToken.None);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Changed += state => { if (state == SystemTunnelState.Failed) changed.TrySetResult(); };
            helper.Lose(); await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(core.ProcessId is null && session.State == SystemTunnelState.Failed);
        });
        await check("Background client heartbeats keep a real pipe lease alive and release cleanly", async () =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var name = HelperPipeIdentity.NewPipeName();
            using var owner = System.Diagnostics.Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var server = new HelperPipeServer(new(journal, backend), name, owner, TimeSpan.FromMilliseconds(700)).RunAsync(timeout.Token);
            await using (var client = new NetworkHelperClient(await HelperPipeClient.ConnectAsync(name, owner, timeout.Token), TimeSpan.FromMilliseconds(100)))
            {
                await client.AcquireAsync(NetworkAcquire(), timeout.Token);
                await Task.Delay(1400, timeout.Token); Assert(!server.IsCompleted && journal.Record?.State == NetworkSessionState.Applied);
                Assert(await client.ReleaseAsync(timeout.Token) == NetworkSessionState.Released);
            }
            await PipeClosedAsync(server); Assert(backend.Original);
        });
        await check("System refusal becomes an answer, and the helper keeps serving", async () =>
        {
            // Найдено живым прогоном: отказ самой системы не был ответом, и помощник
            // завершался посреди живого канала. Окно видело обрыв потока и не могло
            // сказать пользователю ничего, кроме «связь потеряна».
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var name = HelperPipeIdentity.NewPipeName();
            using var owner = System.Diagnostics.Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            backend.BeforeApply = () => throw new IOException("система отказала");
            var server = new HelperPipeServer(new(journal, backend), name, owner, TimeSpan.FromSeconds(4)).RunAsync(timeout.Token);
            var client = new NetworkHelperClient(await HelperPipeClient.ConnectAsync(name, owner, timeout.Token), TimeSpan.FromMilliseconds(100));
            var answered = false;
            try { await client.AcquireAsync(NetworkAcquire(), timeout.Token); }
            catch (UserError error) { answered = error.Message.Contains(nameof(HelperFailure.SystemRefused), StringComparison.Ordinal); }
            Assert(answered);
            // Канал жив: помощник не умер вместе с отказом.
            Assert(!server.IsCompleted);
            // Снятие аренды, которой не случилось, законно отвечает отказом: клиент
            // помечает её до отправки, чтобы потерянное подтверждение не выглядело
            // как отсутствие сессии.
            try { await client.DisposeAsync(); } catch (UserError) { }
            await PipeClosedAsync(server);
        });
    }

    private sealed class SessionCore(List<string> calls) : ITunnelController
    {
        public int Port => 10808;
        public int? ProcessId { get; private set; }
        public ConnectionState State { get; private set; }
        public string CoreLabel => "fixture";
        public CoreStatsEntry? CarriedBy { get; set; }
        public event Action<ConnectionState, string>? Changed;
        public Task StartAsync(Profile profile, CancellationToken token)
        { calls.Add("core:start"); ProcessId = 42; State = ConnectionState.Running; Changed?.Invoke(State, "fixture"); return Task.CompletedTask; }
        public Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token) => Task.FromResult(new ProbeResult(true, 1, "fixture"));
        public void Stop() { calls.Add("core:stop"); ProcessId = null; State = ConnectionState.Idle; }
        public void Dispose() => Stop();
    }

    private sealed class SessionHelper(List<string> calls) : INetworkHelperClient
    {
        public bool FailAcquire;
        public NetworkSessionState ReleaseState = NetworkSessionState.Released;
        public event Action? ConnectionLost;
        public event Action? ProtectionRepaired;
        public void Lose() => ConnectionLost?.Invoke();
        public void Repaired() => ProtectionRepaired?.Invoke();
        public bool Blocking = true;
        public string[] LastPeers = Array.Empty<string>();
        public NetworkLeaseRequest? LastRequest;
        // Как настоящий канал: отмена посреди Acquire закрывает канал, и Release уже не пройдёт.
        public Action? DuringAcquire;
        private bool pipeLost;
        public Task<bool> AcquireAsync(NetworkLeaseRequest request, CancellationToken token)
        {
            calls.Add("helper:acquire"); LastPeers = request.PeerAddresses; LastRequest = request; if (FailAcquire) throw new UserError("fixture");
            DuringAcquire?.Invoke();
            if (token.IsCancellationRequested) { pipeLost = true; throw new OperationCanceledException(token); }
            return Task.FromResult(Blocking);
        }
        public Task<NetworkSessionState> ReleaseAsync(CancellationToken token)
        { calls.Add("helper:release"); if (pipeLost) throw new IOException("pipe closed"); return Task.FromResult(ReleaseState); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

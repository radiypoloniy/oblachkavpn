using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientViewChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("View messages reject executable payloads and snapshots contain no server credentials", () => Sync(() =>
        {
            foreach (var json in new[] { "{}", "{\"Version\":1,\"Version\":1}",
                "{\"Version\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"InstanceId\":\"00000000-0000-0000-0000-000000000000\",\"Operation\":\"Stop\",\"Run\":\"cmd\"}" })
                ExpectUserError(() => HelperProtocol.Parse<ClientViewRequest>(Encoding.UTF8.GetBytes(json)));
            ExpectUserError(() => ClientViewProtocol.Validate(new ClientViewRequest(ClientViewProtocol.Version, Guid.NewGuid(), Guid.Empty, ClientViewOperation.Stop)));
            ExpectUserError(() => ClientViewProtocol.Validate(new ClientViewRequest(ClientViewProtocol.Version + 1, Guid.NewGuid(), Guid.Empty, ClientViewOperation.ReadState)));
            ExpectUserError(() => ClientViewProtocol.Validate(new ClientViewReply(ClientViewProtocol.Version, Guid.NewGuid(), ClientViewFailure.None, null), Guid.NewGuid()));
        }));
        await check("View pipe reads, probes, reconnects and stops without owning the application runtime", async () =>
        {
            var core = new ViewChannelCore(); using var owner = new ClientRuntime(core); SetViewProfile(owner);
            using var current = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            for (var i = 0; i < 2; i++)
            {
                using var endpoint = new ClientViewServer(owner, new SynchronizationContext());
                var serving = endpoint.ServeAsync(current, timeout.Token);
                await using (var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, current, timeout.Token))
                {
                    var read = await client.SendAsync(ClientViewOperation.ReadState, timeout.Token);
                    Assert(read.State?.InstanceId == owner.InstanceId && read.State.Connection == ConnectionState.Running);
                    var json = JsonSerializer.Serialize(read);
                    Assert(!json.Contains("11111111-1111", StringComparison.Ordinal) && !json.Contains("example.com", StringComparison.Ordinal));
                    var probed = await client.SendAsync(ClientViewOperation.Probe, timeout.Token);
                    Assert(probed.State?.Verified == true && core.Probes >= 2);
                    if (i == 1) Assert((await client.SendAsync(ClientViewOperation.Stop, timeout.Token)).State?.Connection == ConnectionState.Idle);
                }
                await serving;
                Assert(!core.Disposed && core.State == (i == 0 ? ConnectionState.Running : ConnectionState.Idle));
            }
            Assert(core.Stops == 1 && owner.Activity.ManualStop && owner.Profiles.Running is null);
        });
        await check("Wrong runtime instance and duplicate view commands cannot mutate the connection", async () =>
        {
            var core = new ViewChannelCore(); using var owner = new ClientRuntime(core); SetViewProfile(owner);
            using var current = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var endpoint = new ClientViewServer(owner, new SynchronizationContext());
            var serving = endpoint.ServeAsync(current, timeout.Token);
            await using (var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(timeout.Token);
                await HelperProtocol.WriteAsync(pipe, new ClientViewRequest(ClientViewProtocol.Version, Guid.NewGuid(), Guid.NewGuid(), ClientViewOperation.Stop), timeout.Token);
                Assert((await HelperProtocol.ReadAsync<ClientViewReply>(pipe, timeout.Token)).Failure == ClientViewFailure.OwnerChanged);
                Assert(core.Stops == 0);
                var request = new ClientViewRequest(ClientViewProtocol.Version, Guid.NewGuid(), owner.InstanceId, ClientViewOperation.Probe);
                await HelperProtocol.WriteAsync(pipe, request, timeout.Token);
                Assert((await HelperProtocol.ReadAsync<ClientViewReply>(pipe, timeout.Token)).Failure == ClientViewFailure.None);
                var probes = core.Probes;
                await HelperProtocol.WriteAsync(pipe, request, timeout.Token);
                Assert((await HelperProtocol.ReadAsync<ClientViewReply>(pipe, timeout.Token)).Failure == ClientViewFailure.InvalidRequest);
                Assert(core.Probes == probes && core.Stops == 0);
            }
            await serving;
        });
        await check("View server rejects a different process before dispatching commands", async () =>
        {
            var core = new ViewChannelCore(); using var owner = new ClientRuntime(core); SetViewProfile(owner);
            using var current = Process.GetCurrentProcess(); using var other = StartHelperIdle();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                using var endpoint = new ClientViewServer(owner, new SynchronizationContext());
                var serving = endpoint.ServeAsync(other, timeout.Token);
                await using var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, current, timeout.Token);
                await ExpectUserErrorAsync(() => serving);
                Assert(core.State == ConnectionState.Running && core.Stops == 0);
            }
            finally { if (!other.HasExited) other.Kill(); await other.WaitForExitAsync(); }
        });
        await check("View client rejects a pipe served by a different process", async () =>
        {
            var core = new ViewChannelCore(); using var owner = new ClientRuntime(core); SetViewProfile(owner);
            using var current = Process.GetCurrentProcess(); using var other = StartHelperIdle();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                using var endpoint = new ClientViewServer(owner, new SynchronizationContext());
                var serving = endpoint.ServeAsync(current, timeout.Token);
                await ExpectUserErrorAsync(async () => { await using var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, other, timeout.Token); });
                await serving; Assert(core.State == ConnectionState.Running);
            }
            finally { if (!other.HasExited) other.Kill(); await other.WaitForExitAsync(); }
        });
        await ClientViewLifetimeChecksAsync(check, root);
    }
    private static void SetViewProfile(ClientRuntime owner)
    {
        var profile = Profiles.Parse(link);
        owner.Profiles.Imported = [profile]; owner.Profiles.Selected = owner.Profiles.Running = profile;
    }
    private sealed class ViewChannelCore : ITunnelController
    {
        public int Probes, Stops; public bool Disposed;
        public TaskCompletionSource? ProbeEntered, ProbeRelease;
        public int Port => 10808;
        public int? ProcessId => State == ConnectionState.Running ? 42 : null;
        public ConnectionState State { get; private set; } = ConnectionState.Running;
        public string CoreLabel => "view-channel-fixture";
        public event Action<ConnectionState, string>? Changed { add { } remove { } }
        public Task StartAsync(Profile profile, CancellationToken token) { State = ConnectionState.Running; return Task.CompletedTask; }
        public async Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token)
        {
            Interlocked.Increment(ref Probes); ProbeEntered?.TrySetResult();
            if (ProbeRelease is not null) await ProbeRelease.Task.WaitAsync(token);
            return new(true, 1, "fixture");
        }
        public void Stop() { Stops++; State = ConnectionState.Idle; }
        public void Dispose() { Disposed = true; Stop(); }
    }
}

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientConnectionsChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var a = Profiles.Parse(link); var b = Profiles.Parse(link.Replace("example.com", "second.invalid").Replace("#Test", "#Second"));
        var c = Profiles.Parse(link.Replace("example.com", "third.invalid").Replace("#Test", "#Third"));
        await check("Application starts a selected proxy and selection of the same server avoids a restart", async () =>
        {
            var core = new ConnectionCore(); using var owner = new ClientRuntime(core);
            owner.Profiles.Imported = [a, b]; owner.Profiles.Selected = a;
            await owner.Connections.StartAsync(a);
            Assert(core.Current == a && owner.Profiles.Running == a && owner.Workspace.WasConnected && owner.Activity.Operation is null);
            await owner.Connections.SelectAsync(a);
            Assert(core.Starts.Count == 1 && core.Stops == 0);
            await owner.Connections.SelectAsync(b);
            Assert(core.Starts.SequenceEqual(new[] { a, b }) && core.Current == b && owner.Profiles.Selected == b);
            await owner.Connections.StopAsync(); Assert(core.State == ConnectionState.Idle && !owner.Workspace.WasConnected);
        });
        await check("A slow start coalesces intermediate selections and applies the last choice", async () =>
        {
            var core = new ConnectionCore { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var owner = new ClientRuntime(core); owner.Profiles.Imported = [a, b, c];
            var starting = owner.Connections.StartAsync(a);
            await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second = owner.Connections.SelectAsync(b); var last = owner.Connections.SelectAsync(c);
            core.Release.SetResult(); await Task.WhenAll(starting, second, last);
            Assert(core.Starts.SequenceEqual(new[] { a, c }) && owner.Profiles.Running == c && owner.Profiles.Selected == c);
            Assert(owner.Activity.Operation is null && !owner.Connections.IsActive);
        });
        await check("Stopping a pending start cancels it without a late reconnect or duplicate core stop", async () =>
        {
            var core = new ConnectionCore { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var owner = new ClientRuntime(core); owner.Profiles.Imported = [a];
            var starting = owner.Connections.StartAsync(a);
            await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(owner.Connections.StopAsync(), owner.Connections.StopAsync());
            try { await starting; throw new InvalidOperationException("Cancelled start succeeded"); } catch (OperationCanceledException) { }
            Assert(core.State == ConnectionState.Idle && core.Stops == 1 && owner.Profiles.Running is null && !owner.Workspace.WasConnected);
            Assert(owner.Connections.LastResult == ClientConnectionResult.Cancelled && owner.Activity.Operation is null);
        });
        await check("Removed nodes and unapproved free nodes are rejected before starting a core", async () =>
        {
            var core = new ConnectionCore(); using var owner = new ClientRuntime(core); owner.Profiles.Imported = [a];
            await ExpectUserErrorAsync(() => owner.Connections.StartAsync(b));
            var free = a with { Source = ProfileSource.Free, Group = FreeServers.GroupId };
            owner.Profiles.Imported = [free];
            await ExpectUserErrorAsync(() => owner.Connections.StartAsync(free));
            Assert(core.Starts.Count == 0 && owner.Activity.Operation is null);
            owner.Workspace.FreeConsent = FreeServers.ConsentVersion;
            await owner.Connections.StartAsync(free); Assert(core.Current == free);
        });
        await check("Application connection controller switches a TUN inside its existing protection", async () =>
        {
            var core = new RecoveryCore { Working = a }; var host = new TunnelHostFixture();
            using var owner = new ClientRuntime(core, host); owner.Profiles.Imported = [a]; owner.Preferences.SystemTunnel = true;
            await owner.Connections.StartAsync(a); var session = owner.Tunnel;
            await owner.Connections.StartAsync(a);
            Assert(ReferenceEquals(session, owner.Tunnel) && owner.Tunnel?.BlocksBypass == true && host.Connects == 1 && host.Disposes == 0);
            await owner.Connections.StopAsync(); Assert(owner.Tunnel is null && core.State == ConnectionState.Idle && host.Disposes == 1);
        });
        await check("Switching outside a TUN lease requires explicit permission to rebuild it", async () =>
        {
            var core = new RecoveryCore { Working = a }; var host = new TunnelHostFixture();
            using var owner = new ClientRuntime(core, host); owner.Profiles.Imported = [a]; owner.Preferences.SystemTunnel = true;
            await owner.Connections.StartAsync(a); var session = owner.Tunnel;
            var free = b with { Source = ProfileSource.Free, Group = FreeServers.GroupId };
            owner.Profiles.Imported = [a, free]; owner.Workspace.FreeConsent = FreeServers.ConsentVersion;
            await ExpectUserErrorAsync(() => owner.Connections.StartAsync(free));
            Assert(ReferenceEquals(session, owner.Tunnel) && host.Connects == 1);
            await owner.Connections.StartAsync(free, allowReconnect: true);
            Assert(!ReferenceEquals(session, owner.Tunnel) && host.Connects == 2 && owner.Tunnels.ForeignSession && owner.Profiles.Running == free);
            await owner.Connections.StopAsync();
        });
        await check("Runtime disposal cancels a start owned by the application", async () =>
        {
            var core = new ConnectionCore { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var owner = new ClientRuntime(core); owner.Profiles.Imported = [a];
            var starting = owner.Connections.StartAsync(a); await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            owner.Dispose();
            try { await starting; throw new InvalidOperationException("Disposed runtime kept starting"); } catch (OperationCanceledException) { }
            Assert(core.Disposed && core.State == ConnectionState.Idle && !owner.Connections.IsActive);
        });
        await check("A manual connection request cancels and waits for an outstanding background health check", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var core = new MonitorCore { Probe = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(true, 1, "fixture"); } };
            using var owner = new ClientRuntime(core); owner.Profiles.Imported = [a]; owner.Profiles.Selected = a;
            owner.Monitor.NextCheck = DateTimeOffset.MinValue; var checking = owner.Monitor.PollAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await owner.Connections.StartAsync(a); await checking;
            Assert(owner.Profiles.Running == a && owner.Activity.Operation is null && !owner.Activity.BackgroundCheck && core.Probes == 2);
        });
        await check("A failed start clears ownership and can be retried", async () =>
        {
            var core = new ConnectionCore { FailStart = true }; using var owner = new ClientRuntime(core); owner.Profiles.Imported = [a];
            await ExpectUserErrorAsync(() => owner.Connections.StartAsync(a));
            Assert(owner.Connections.LastResult == ClientConnectionResult.Failed && owner.Activity.Operation is null && !owner.Connections.IsActive);
            core.FailStart = false; await owner.Connections.StartAsync(a);
            Assert(owner.Profiles.Running == a && core.State == ConnectionState.Running);
        });
        await ClientConnectionIpcChecksAsync(check);
        await check("A WPF view can start, change server, exit during startup and reconnect to the same owner", async () =>
        {
            var core = new ConnectionCore { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var owner = new ClientRuntime(core); owner.Profiles.Imported = [a, b]; owner.Profiles.Selected = a;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var folder = Path.Combine(AppContext.BaseDirectory, "test-data"); Directory.CreateDirectory(folder);
            var report = Path.Combine(folder, "start-view-" + Guid.NewGuid().ToString("N") + ".json");
            await ViewChildAsync(owner, "start-crash", report, timeout.Token);
            Assert(owner.Connections.IsActive && core.State == ConnectionState.Starting && !core.Disposed);
            core.Release.SetResult(); await owner.Connections.Pending!.WaitAsync(timeout.Token);
            await ViewChildAsync(owner, "connect", report, timeout.Token);
            Assert(core.State == ConnectionState.Running && core.Current == b && owner.Profiles.Running == b);
            await owner.Connections.StopAsync();
        });
    }
    private sealed class ConnectionCore : ITunnelController
    {
        public List<Profile> Starts { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        public Profile? Current; public int Stops; public bool Disposed, FailStart;
        public int Port => 10808;
        public int? ProcessId => State == ConnectionState.Running ? 42 : null;
        public ConnectionState State { get; private set; }
        public string CoreLabel => "connection-fixture";
        public event Action<ConnectionState, string>? Changed;
        public async Task StartAsync(Profile profile, CancellationToken token)
        {
            Starts.Add(profile); State = ConnectionState.Starting; Changed?.Invoke(State, "fixture"); Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(token);
            if (FailStart) { State = ConnectionState.Failed; throw new UserError("fixture startup failure"); }
            token.ThrowIfCancellationRequested(); Current = profile; State = ConnectionState.Running; Changed?.Invoke(State, "fixture");
        }
        public Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token) => Task.FromResult(new ProbeResult(true, 1, "fixture"));
        public void Stop() { Stops++; State = ConnectionState.Idle; Changed?.Invoke(State, "fixture"); }
        public void Dispose() { Disposed = true; Stop(); }
    }
}

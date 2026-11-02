using System.Diagnostics;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientPauseChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var a = Profiles.Parse(link); var b = Profiles.Parse(link.Replace("example.com", "second.invalid"));
        await check("Pause resumes once after its wall-clock deadline without a view, including a long sleep", async () =>
        {
            var now = DateTimeOffset.UtcNow; var core = new ConnectionCore();
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), pauseNow: () => now);
            owner.Profiles.Imported = [a]; await owner.Connections.StartAsync(a);
            await owner.Pause.PauseAsync(5);
            Assert(owner.Pause.Until == now.AddMinutes(5) && core.State == ConnectionState.Idle && owner.Workspace.WasConnected);
            now = now.AddMinutes(4); await owner.Pause.PollAsync(); Assert(core.Starts.Count == 1);
            now = now.AddHours(2); await Task.WhenAll(owner.Pause.PollAsync(), owner.Pause.PollAsync());
            Assert(owner.Pause.Until is null && core.Starts.Count == 2 && core.Current == a && !owner.Activity.ManualStop);
        });
        await check("Cancelling pause, explicit Stop and manual Connect prevent a later automatic start", async () =>
        {
            var now = DateTimeOffset.UtcNow; var core = new ConnectionCore();
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), pauseNow: () => now);
            owner.Profiles.Imported = [a, b]; await owner.Connections.StartAsync(a);
            await owner.Pause.PauseAsync(5); owner.Pause.Cancel();
            now = now.AddMinutes(6); await owner.Pause.PollAsync();
            Assert(core.Starts.Count == 1 && !owner.Workspace.WasConnected);
            await owner.Connections.StartAsync(a); await owner.Pause.PauseAsync(15); await owner.Connections.StopAsync();
            now = now.AddHours(1); await owner.Pause.PollAsync(); Assert(core.Starts.Count == 2 && owner.Pause.Until is null);
            await owner.Connections.StartAsync(a); await owner.Pause.PauseAsync(60); await owner.Connections.StartAsync(b);
            now = now.AddHours(2); await owner.Pause.PollAsync(); Assert(core.Starts.Count == 4 && core.Current == b && owner.Pause.Until is null);
        });
        await check("Expired pause waits for an unrelated operation instead of stealing it", async () =>
        {
            var now = DateTimeOffset.UtcNow; var core = new ConnectionCore();
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), pauseNow: () => now);
            owner.Profiles.Imported = [a]; await owner.Connections.StartAsync(a); await owner.Pause.PauseAsync(5);
            using var busy = new CancellationTokenSource(); owner.Activity.Operation = busy;
            now = now.AddMinutes(6); await owner.Pause.PollAsync();
            Assert(core.Starts.Count == 1 && owner.Pause.Until is not null && !busy.IsCancellationRequested);
            owner.Activity.Operation = null; await owner.Pause.PollAsync(); Assert(core.Starts.Count == 2);
        });
        await check("Pause restores TUN only after the old protected session has been released", async () =>
        {
            var now = DateTimeOffset.UtcNow; var core = new RecoveryCore { Working = a }; var host = new TunnelHostFixture();
            using var owner = new ClientRuntime(core, host, pauseNow: () => now); owner.Preferences.SystemTunnel = true;
            owner.Profiles.Imported = [a]; await owner.Connections.StartAsync(a);
            await owner.Pause.PauseAsync(5); Assert(owner.Tunnel is null && host.Disposes == 1 && core.State == ConnectionState.Idle);
            now = now.AddMinutes(6); await owner.Pause.PollAsync();
            Assert(owner.Tunnel?.BlocksBypass == true && host.Connects == 2); await owner.Connections.StopAsync();
        });
        await check("Pause resume failure is reported once and never becomes a repeating timer retry", async () =>
        {
            var now = DateTimeOffset.UtcNow; var core = new ConnectionCore();
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), pauseNow: () => now);
            owner.Profiles.Imported = [a]; await owner.Connections.StartAsync(a); await owner.Pause.PauseAsync(5);
            var failures = 0; owner.Pause.ResumeFailed += () => failures++; core.FailStart = true;
            now = now.AddMinutes(6); await owner.Pause.PollAsync(); await owner.Pause.PollAsync();
            Assert(failures == 1 && core.Starts.Count == 2 && owner.Pause.Until is null && !owner.Connections.IsActive);
        });
        await check("Pause rejects invalid durations and application disposal prevents resume", async () =>
        {
            var core = new ConnectionCore(); var owner = new ClientRuntime(core); owner.Profiles.Imported = [a];
            await owner.Connections.StartAsync(a); await ExpectUserErrorAsync(() => owner.Pause.PauseAsync(1));
            Assert(core.Stops == 0 && owner.Pause.Until is null);
            await owner.Pause.PauseAsync(5); owner.Dispose(); await owner.Pause.PollAsync(); Assert(core.Starts.Count == 1);
        });
        await check("Pause survives IPC disconnection and a new view can read or cancel its deadline", async () =>
        {
            var core = new ConnectionCore(); using var owner = new ClientRuntime(core); owner.Profiles.Imported = [a];
            await owner.Connections.StartAsync(a);
            using var current = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using (var endpoint = new ClientViewServer(owner, new SynchronizationContext()))
            {
                var serving = endpoint.ServeAsync(current, timeout.Token);
                await using (var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, current, timeout.Token))
                {
                    var reply = await client.SendAsync(ClientViewOperation.Pause, timeout.Token, pauseMinutes: 15);
                    Assert(reply.State?.PausedUntil is not null && reply.State.Connection == ConnectionState.Idle);
                }
                await serving;
            }
            Assert(owner.Pause.Until is not null);
            using var next = new ClientViewServer(owner, new SynchronizationContext()); var connected = next.ServeAsync(current, timeout.Token);
            await using (var client = await ClientViewClient.ConnectAsync(next.PipeName, current, timeout.Token))
            {
                Assert((await client.SendAsync(ClientViewOperation.ReadState, timeout.Token)).State?.PausedUntil == owner.Pause.Until);
                Assert((await client.SendAsync(ClientViewOperation.CancelPause, timeout.Token)).State?.PausedUntil is null);
            }
            await connected; Assert(owner.Pause.Until is null && !owner.Workspace.WasConnected && core.Starts.Count == 1);
        });
        await ClientSelectionChecksAsync(check);
    }
}

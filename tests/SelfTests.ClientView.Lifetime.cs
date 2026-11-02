using System.Diagnostics;
using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientViewLifetimeChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("View disconnection during a probe leaves the runtime and its operation alive", async () =>
        {
            var core = new ViewChannelCore { ProbeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ProbeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var owner = new ClientRuntime(core); SetViewProfile(owner);
            using var current = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var endpoint = new ClientViewServer(owner, new SynchronizationContext());
            var serving = endpoint.ServeAsync(current, timeout.Token);
            var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, current, timeout.Token);
            var pending = client.SendAsync(ClientViewOperation.Probe, timeout.Token);
            await core.ProbeEntered.Task.WaitAsync(timeout.Token);
            Assert((await pending).State?.Busy == true);
            await client.DisposeAsync();
            Assert(!core.ProbeRelease.Task.IsCompleted && core.State == ConnectionState.Running && core.Stops == 0);
            core.ProbeRelease.SetResult();
            await owner.Monitor.PendingCheck!.WaitAsync(timeout.Token);
            await serving;
            Assert(core.State == ConnectionState.Running && !core.Disposed && owner.Activity.Operation is null);
        });
        await check("Remote stop interrupts an outstanding health probe through the same view channel", async () =>
        {
            var core = new ViewChannelCore { ProbeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ProbeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var owner = new ClientRuntime(core); SetViewProfile(owner);
            using var current = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var endpoint = new ClientViewServer(owner, new SynchronizationContext());
            var serving = endpoint.ServeAsync(current, timeout.Token);
            await using (var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, current, timeout.Token))
            {
                Assert((await client.SendAsync(ClientViewOperation.Probe, timeout.Token)).State?.Busy == true);
                var stopped = await client.SendAsync(ClientViewOperation.Stop, timeout.Token);
                Assert(stopped.State?.Connection == ConnectionState.Idle && stopped.State.StopRequested && !stopped.State.Busy);
                Assert(core.Stops == 1 && !core.ProbeRelease.Task.IsCompleted && owner.Activity.Operation is null);
            }
            await serving;
        });
        await check("Remote stop releases a borrowed TUN session before stopping its core", async () =>
        {
            var selected = Profiles.Parse(link); var host = new TunnelHostFixture(); var core = new RecoveryCore { Working = selected };
            using var owner = new ClientRuntime(core, host); owner.Preferences.SystemTunnel = true;
            owner.Profiles.Selected = selected; owner.Profiles.Imported = [selected];
            await owner.Tunnels.StartAsync(selected, CancellationToken.None);
            using var current = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var endpoint = new ClientViewServer(owner, new SynchronizationContext());
            var serving = endpoint.ServeAsync(current, timeout.Token);
            await using (var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, current, timeout.Token))
            {
                var stopped = await client.SendAsync(ClientViewOperation.Stop, timeout.Token);
                Assert(stopped.State?.Connection == ConnectionState.Idle && stopped.State.Tunnel is null && stopped.State.Verified is null);
            }
            await serving;
            Assert(owner.Tunnel is null && host.Disposes == 1 && !owner.Workspace.WasConnected);
        });
        await check("Four separate WPF views reconnect, survive a view crash, and stop only on explicit command", async () =>
        {
            var core = new ViewChannelCore(); using var owner = new ClientRuntime(core); SetViewProfile(owner);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            foreach (var mode in new[] { "close", "crash", "close", "stop" })
            {
                var report = Path.Combine(root, "view-" + Guid.NewGuid().ToString("N") + ".json");
                await ViewChildAsync(owner, mode, report, timeout.Token);
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
                Assert(json.RootElement.GetProperty("Passed").GetBoolean() && json.RootElement.GetProperty("WindowLoaded").GetBoolean());
                Assert(json.RootElement.GetProperty("InstanceId").GetGuid() == owner.InstanceId);
                Assert(!core.Disposed && core.State == (mode == "stop" ? ConnectionState.Idle : ConnectionState.Running));
            }
            Assert(core.Stops == 1);
        });
    }
    private static async Task ViewChildAsync(ClientRuntime owner, string mode, string report, CancellationToken token)
    {
        using var endpoint = new ClientViewServer(owner, new SynchronizationContext());
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Oblachka.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--view-channel-fixture", endpoint.PipeName, Environment.ProcessId.ToString(), report, mode }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var serving = endpoint.ServeAsync(child, token);
        try
        {
            await child.WaitForExitAsync(token);
            await serving;
            if (child.ExitCode != (mode is "crash" or "start-crash" ? 23 : 0)) throw new InvalidOperationException("WPF view failed: " + await File.ReadAllTextAsync(report));
        }
        finally
        {
            endpoint.Dispose();
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            try { await serving; } catch (OperationCanceledException) { }
        }
    }
}

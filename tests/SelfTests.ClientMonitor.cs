namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientMonitorChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Application health loop probes without a window and stops on runtime disposal", async () =>
        {
            var core = new MonitorCore(); using var runtime = new ClientRuntime(core);
            runtime.Monitor.ObserveProcess(); runtime.Monitor.NextCheck = DateTimeOffset.MinValue;
            var finished = new TaskCompletionSource<HealthObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Monitor.Completed += observation => finished.TrySetResult(observation);
            runtime.Monitor.Start(new SynchronizationContext());
            var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(result.Round.Verified && runtime.Monitor.LastProbe?.Success == true && core.Probes >= 2);
            Assert(runtime.Activity.Operation is null && !runtime.Activity.BackgroundCheck);
            var probes = core.Probes;
            runtime.Dispose(); runtime.Monitor.NextCheck = DateTimeOffset.MinValue;
            await Task.Delay(100);
            Assert(core.Probes == probes && core.Disposed);
        });
        await check("Headless health policy preserves fresh, sibling, auto-select and idle/traffic thresholds", async () =>
        {
            var core = new MonitorCore { Success = false }; using var runtime = new ClientRuntime(core);
            var clock = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
            using var monitor = new ClientHealthMonitor(runtime, () => clock);
            monitor.NextCheck = clock; await monitor.PollAsync();
            Assert(monitor.LastObservation is { FailureLimit: 2, ShouldRecover: false } && monitor.NextCheck == clock.AddSeconds(3));
            clock += TimeSpan.FromSeconds(3); await monitor.PollAsync();
            Assert(monitor.LastObservation is { ShouldRecover: true } && monitor.Health.FailedRounds == 2);
            monitor.Health.Reset(); monitor.VerifiedSinceStart = true; runtime.Profiles.AutoSelect = true;
            monitor.NextCheck = clock; await monitor.PollAsync();
            Assert(monitor.LastObservation is { FailureLimit: 2, ShouldRecover: false } && monitor.NextCheck == clock.AddSeconds(8));
            runtime.Profiles.AutoSelect = false; monitor.NextCheck = clock; await monitor.PollAsync();
            Assert(monitor.LastObservation is { FailureLimit: 3, ShouldRecover: false });
            core.Success = true; monitor.NextCheck = clock; await monitor.PollAsync();
            Assert(monitor.Health.FailedRounds == 0 && monitor.VerifiedSinceStart && monitor.NextCheck == clock.AddSeconds(45));
            monitor.LastTraffic = clock; monitor.NextCheck = clock; await monitor.PollAsync();
            Assert(monitor.NextCheck == clock.AddSeconds(20));
            var profile = Profiles.Parse(link); runtime.Profiles.Selected = profile;
            runtime.Profiles.Imported = [profile, profile with { Name = "Sibling" }]; runtime.Profiles.MachineMode = true;
            core.Success = false; monitor.Health.Reset(); monitor.StartedFresh(); monitor.NextCheck = clock;
            await monitor.PollAsync(); Assert(monitor.LastObservation is { FailureLimit: 1, ShouldRecover: true });
        });
        await check("Monitoring never overlaps operations, ignores stopped/VM sessions and cancels its own probe", async () =>
        {
            var core = new MonitorCore(); using var runtime = new ClientRuntime(core);
            var monitor = runtime.Monitor; monitor.NextCheck = DateTimeOffset.MinValue;
            using var busy = new CancellationTokenSource(); runtime.Activity.Operation = busy;
            await monitor.PollAsync(); Assert(core.Probes == 0);
            runtime.Activity.Operation = null; runtime.Activity.RecoveryWait = busy;
            await monitor.PollAsync(); Assert(core.Probes == 0);
            runtime.Activity.RecoveryWait = null; runtime.Activity.ManualStop = true;
            await monitor.PollAsync(); Assert(core.Probes == 0);
            runtime.Activity.ManualStop = false; runtime.Activity.VmTestActive = true;
            await monitor.PollAsync(); Assert(core.Probes == 0);
            runtime.Activity.VmTestActive = false;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.Probe = async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(true, 1, "fixture"); };
            var poll = monitor.PollAsync(); await started.Task;
            await monitor.PollAsync(); Assert(core.Probes == 2 && runtime.Activity.BackgroundCheck);
            monitor.Dispose(); await poll;
            Assert(runtime.Activity.Operation is null && !runtime.Activity.EngineOperation && monitor.LastObservation is null);
        });
        await check("Application path checks distinguish tunnel and direct failures without a WPF window", async () =>
        {
            var success = false; var failDirect = false;
            var path = new ClientPathHealth((target, _) =>
            {
                var reachable = success && !(failDirect && SystemPathProbe.DirectTargets.Contains(target));
                return Task.FromResult(new ProbeResult(reachable, 1, "fixture", reachable ? ProbeFailure.None : ProbeFailure.Reachability));
            });
            Assert(await path.CheckAsync(true, false, CancellationToken.None) is not null && !path.ShouldRepair);
            Assert(await path.CheckAsync(true, false, CancellationToken.None) is not null && path.ShouldRepair && !path.DirectOnly);
            success = true; Assert(await path.CheckAsync(true, true, CancellationToken.None) is null && !path.ShouldRepair);
            failDirect = true;
            Assert(await path.CheckAsync(true, true, CancellationToken.None) is not null && path.DirectOnly && !path.ShouldRepair);
            Assert(await path.CheckAsync(true, true, CancellationToken.None) is not null && path.DirectOnly && path.ShouldRepair);
            path.Reset(); Assert(!path.Broken && !path.Rebuilt && path.Tunnel.FailedRounds == 0);
        });
    }

    private sealed class MonitorCore : ITunnelController
    {
        public bool Success = true, Disposed;
        public int Probes;
        public Func<CancellationToken, Task<ProbeResult>>? Probe;
        public int Port => 10808;
        public int? ProcessId => Disposed ? null : 42;
        public ConnectionState State => Disposed ? ConnectionState.Idle : ConnectionState.Running;
        public string CoreLabel => "fixture";
        public event Action<ConnectionState, string>? Changed { add { } remove { } }
        public Task StartAsync(Profile profile, CancellationToken token) => Task.CompletedTask;
        public Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token)
        { Interlocked.Increment(ref Probes); return Probe?.Invoke(token) ?? Task.FromResult(new ProbeResult(Success, 1, "fixture", Success ? ProbeFailure.None : ProbeFailure.Reachability)); }
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
}

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientRecoveryChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var first = Profiles.Parse(link);
        var second = Profiles.Parse(link.Replace("type=xhttp", "type=tcp").Replace("#Test", "#Second"));
        await check("Monitoring restores a failed protocol without any WPF window or external diagnosis", async () =>
        {
            var core = new RecoveryCore { Working = second }; using var runtime = new ClientRuntime(core);
            await core.StartAsync(first, CancellationToken.None);
            runtime.Profiles.Imported = [first, second]; runtime.Profiles.Selected = runtime.Profiles.Running = first;
            runtime.Profiles.MachineMode = true;
            var completed = new TaskCompletionSource<RecoveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Recovery.Completed += outcome => completed.TrySetResult(outcome);
            runtime.Monitor.ObserveProcess(); runtime.Monitor.NextCheck = DateTimeOffset.MinValue;
            runtime.Monitor.Start(new SynchronizationContext());
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(result.Kind == RecoveryOutcomeKind.Recovered && ReferenceEquals(runtime.Profiles.Running, second));
            Assert(runtime.Monitor.LastProbe?.Success == true && runtime.Activity.Operation is null && !runtime.Activity.FindingRoute);
            var restarted = new TaskCompletionSource<RecoveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Recovery.Completed += outcome => { if (outcome.Kind == RecoveryOutcomeKind.Restarted) restarted.TrySetResult(outcome); };
            core.Fail(); await restarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(core.State == ConnectionState.Running && ReferenceEquals(runtime.Profiles.Running, second));
        });
        await check("Headless recovery switches only within a live TUN lease and never releases its protection", async () =>
        {
            var calls = new List<string>(); var core = new RecoveryCore { Working = second }; var helper = new SessionHelper(calls);
            using var runtime = new ClientRuntime(core);
            await using var session = new SystemTunnelSession(core, helper, ownsResources: false);
            await session.StartWithSparesAsync([new(first, ["192.0.2.1"]), new(second, ["192.0.2.1"])], CancellationToken.None);
            runtime.Profiles.Imported = [first, second]; runtime.Profiles.Selected = runtime.Profiles.Running = first;
            runtime.Profiles.MachineMode = true; runtime.Preferences.SystemTunnel = true;
            using var recovery = new ClientRecovery(runtime, () => session, (_, _) => throw new InvalidOperationException("Sibling must skip diagnosis"), (_, _) => Task.CompletedTask);
            var releases = calls.Count(c => c == "helper:release");
            await recovery.RecoverAsync();
            Assert(recovery.LastOutcome is { Kind: RecoveryOutcomeKind.Recovered } && ReferenceEquals(runtime.Profiles.Running, second));
            Assert(session.State == SystemTunnelState.Running && session.BlocksBypass && calls.Count(c => c == "helper:release") == releases);
        });
        await check("An unleased protocol requests reconnection without stopping TUN or starting a direct fallback", async () =>
        {
            var calls = new List<string>(); var core = new RecoveryCore { Working = second }; var helper = new SessionHelper(calls);
            using var runtime = new ClientRuntime(core);
            await using var session = new SystemTunnelSession(core, helper, ownsResources: false);
            await session.StartAsync(new(first, ["192.0.2.1"]), CancellationToken.None);
            runtime.Profiles.Imported = [first, second]; runtime.Profiles.Selected = runtime.Profiles.Running = first;
            runtime.Profiles.MachineMode = true; runtime.Preferences.SystemTunnel = true;
            using var recovery = new ClientRecovery(runtime, () => session, wait: (_, _) => Task.CompletedTask);
            var starts = core.Starts;
            await recovery.RecoverAsync();
            Assert(recovery.LastOutcome is { Kind: RecoveryOutcomeKind.RequiresReconnect, Selected: not null });
            Assert(core.Starts == starts && session.BlocksBypass && !calls.Contains("helper:release"));
            Assert(ReferenceEquals(runtime.Profiles.Running, first));
        });
        await check("Core restart respects manual stop, free nodes, disabled recovery and shared operation ownership", async () =>
        {
            var core = new RecoveryCore { Working = first }; using var runtime = new ClientRuntime(core);
            runtime.Profiles.Selected = runtime.Profiles.Running = first; runtime.Profiles.Imported = [first];
            using var recovery = new ClientRecovery(runtime, wait: (_, _) => Task.CompletedTask);
            core.Fail(); await recovery.RestartFailedAsync();
            Assert(recovery.LastOutcome is { Kind: RecoveryOutcomeKind.Restarted } && core.State == ConnectionState.Running);
            var starts = core.Starts;
            core.Fail(); runtime.Activity.ManualStop = true; await recovery.RestartFailedAsync();
            Assert(core.Starts == starts); runtime.Activity.ManualStop = false;
            runtime.Preferences.AutoRecover = false; await recovery.RecoverAsync(); Assert(core.Starts == starts);
            runtime.Preferences.AutoRecover = true;
            using var busy = new CancellationTokenSource(); runtime.Activity.Operation = busy;
            await recovery.RestartFailedAsync(); Assert(core.Starts == starts && ReferenceEquals(runtime.Activity.Operation, busy));
            runtime.Activity.Operation = null;
            runtime.Preferences.SystemTunnel = true; await recovery.RestartFailedAsync();
            Assert(recovery.LastOutcome is { Kind: RecoveryOutcomeKind.RequiresReconnect } && core.Starts == starts);
            runtime.Profiles.Selected = runtime.Profiles.Running = first with { Source = ProfileSource.Free };
            await recovery.RestartFailedAsync(); await recovery.RecoverAsync(); Assert(core.Starts == starts);
        });
        await check("Headless recovery exhausts the same three-attempt budget and leaves TUN reconnection explicit", async () =>
        {
            var core = new RecoveryCore(); using var runtime = new ClientRuntime(core);
            runtime.Profiles.Selected = runtime.Profiles.Running = first; runtime.Profiles.Imported = [first];
            runtime.Preferences.SystemTunnel = true;
            using var recovery = new ClientRecovery(runtime, wait: (_, _) => Task.CompletedTask);
            for (var i = 0; i < 3; i++) { await recovery.RecoverAsync(); Assert(recovery.LastOutcome?.Kind == RecoveryOutcomeKind.RequiresReconnect); }
            await recovery.RecoverAsync();
            Assert(recovery.LastOutcome?.Kind == RecoveryOutcomeKind.Exhausted && core.Starts == 0 && runtime.Monitor.NextCheck == DateTimeOffset.MaxValue);
        });
        await check("Cancellation during recovery delay releases ownership without choosing or starting another server", async () =>
        {
            var core = new RecoveryCore { Working = second }; using var runtime = new ClientRuntime(core);
            runtime.Profiles.Selected = runtime.Profiles.Running = first; runtime.Profiles.Imported = [first, second];
            runtime.Profiles.MachineMode = true;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var recovery = new ClientRecovery(runtime, wait: async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
            var task = recovery.RecoverAsync(); await started.Task;
            runtime.Activity.ManualStop = true; runtime.Activity.Operation!.Cancel(); await task;
            Assert(core.Starts == 0 && ReferenceEquals(runtime.Profiles.Selected, first) && recovery.LastOutcome is null);
            Assert(runtime.Activity.Operation is null && !runtime.Activity.FindingRoute);
        });
    }

    private sealed class RecoveryCore : ITunnelController
    {
        public Profile? Working, Current;
        public int Starts;
        public int Port => 10808;
        public int? ProcessId => State == ConnectionState.Running ? 42 : null;
        public ConnectionState State { get; private set; }
        public string CoreLabel => "fixture";
        public event Action<ConnectionState, string>? Changed;
        public Task StartAsync(Profile profile, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Starts++; Current = profile; State = ConnectionState.Running; Changed?.Invoke(State, "fixture"); return Task.CompletedTask; }
        public Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token)
        { var ok = Current == Working; return Task.FromResult(new ProbeResult(ok, 1, "fixture", ok ? ProbeFailure.None : ProbeFailure.Reachability)); }
        public void Fail() { State = ConnectionState.Failed; Changed?.Invoke(State, "fixture"); }
        public void Stop() { State = ConnectionState.Idle; }
        public void Dispose() => Stop();
    }
}

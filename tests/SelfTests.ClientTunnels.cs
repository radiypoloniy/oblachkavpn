namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientTunnelsChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var selected = Profiles.Parse(link);
        await check("A lost helper channel rebuilds TUN autonomously with rules edited after initial startup", async () =>
        {
            var host = new TunnelHostFixture(); var core = new RecoveryCore { Working = selected };
            var path = new ClientPathHealth((_, _) => Task.FromResult(new ProbeResult(true, 1, "fixture")));
            using var runtime = new ClientRuntime(core, host, path);
            runtime.Preferences.SystemTunnel = true; runtime.Profiles.Selected = selected; runtime.Profiles.Imported = [selected];
            await runtime.Tunnels.StartAsync(selected, CancellationToken.None);
            runtime.TunnelPolicy.Sites.Add(RoutingPlan.Create("changed.invalid", SiteAction.Block));
            var completed = new TaskCompletionSource<RecoveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Recovery.Completed += result => { if (result.Kind == RecoveryOutcomeKind.Rebuilt) completed.TrySetResult(result); };
            runtime.Monitor.Start(new SynchronizationContext());
            var old = host.LastLink!; old.Fixture.Lose();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            if (host.Connects != 2 || host.Disposes != 1 || old.Fixture.Subscribers != 0)
                throw new InvalidOperationException($"Rebuild lifecycle: connects={host.Connects}, disposes={host.Disposes}, oldSubscribers={old.Fixture.Subscribers}; {string.Join(',', host.Calls)}");
            Assert(host.Requests[^1].Sites!.Any(r => r.Domain == "changed.invalid") && runtime.Tunnel?.BlocksBypass == true);
            Assert(runtime.Activity.Operation is null && !runtime.Activity.EngineOperation);
            await runtime.StopTunnelAsync();
        });
        await check("Headless TUN rebuild uses current rules, routing, DNS and tuning rather than its original snapshot", async () =>
        {
            var host = new TunnelHostFixture(); var core = new RecoveryCore { Working = selected };
            using var runtime = new ClientRuntime(core, host); runtime.Preferences.SystemTunnel = true;
            runtime.Profiles.Selected = selected; runtime.Profiles.Imported = [selected];
            runtime.TunnelPolicy.Sites.Add(RoutingPlan.Create("first.invalid", SiteAction.Block));
            runtime.TunnelPolicy.Programs.Add(ProgramRoutingPlan.Create("first.exe", SiteAction.Block));
            await runtime.Tunnels.StartAsync(selected, CancellationToken.None);
            Assert(runtime.Tunnel?.BlocksBypass == true && runtime.Tunnels.CanRebuild);
            var old = host.LastLink!;
            runtime.TunnelPolicy.Sites.Clear(); runtime.TunnelPolicy.Sites.Add(RoutingPlan.Create("second.invalid", SiteAction.Direct));
            runtime.TunnelPolicy.Programs.Clear(); runtime.TunnelPolicy.Programs.Add(ProgramRoutingPlan.Create("second.exe", SiteAction.Tunnel));
            runtime.TunnelPolicy.ProviderRouting = new("fixture", ["domain:list.invalid"], [], [], [], [], []);
            runtime.TunnelPolicy.UseProviderRouting = true;
            runtime.TunnelPolicy.Advanced = AdvancedOptions.Default with { Mtu = 1300, NoIpv6 = true };
            runtime.Preferences.LocalNetwork = false; runtime.Preferences.QuicOverTcp = true; runtime.Preferences.CompatibleStack = true;
            await runtime.Recovery.RepairPathAsync("fixture path failure");
            var request = host.Requests[^1];
            Assert(host.Connects == 2 && host.Disposes == 1 && old.Fixture.Subscribers == 0);
            Assert(request.Sites!.Any(r => r.Domain == "second.invalid") && request.Sites!.All(r => r.Domain != "first.invalid"));
            Assert(request.Programs!.Any(r => r.Executable == "second.exe") && request.Tuning is { Mtu: 1300, NoIpv6: true });
            Assert(!request.LocalNetwork && request.QuicOverTcp && request.CompatibleStack && request.Lists is not null);
            Assert(host.Domains[^1].Contains("second.invalid") && host.Domains[^1].Contains("list.invalid"));
            Assert(runtime.Recovery.LastOutcome?.Kind == RecoveryOutcomeKind.Rebuilt && runtime.Tunnel?.BlocksBypass == true);
            var connects = host.Connects;
            await runtime.Recovery.RepairPathAsync("fixture still broken"); await runtime.Recovery.RepairPathAsync("fixture still broken");
            Assert(host.Connects == connects && runtime.Monitor.Path.Broken && runtime.Recovery.NetworkClosed);
            runtime.Recovery.PathHealthy(); Assert(!runtime.Monitor.Path.Broken && !runtime.Monitor.Path.Rebuilt);
            await runtime.StopTunnelAsync();
        });
        await check("Changes made during endpoint preparation reach the newly acquired lease", async () =>
        {
            var host = new TunnelHostFixture(); using var runtime = new ClientRuntime(new RecoveryCore(), host);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Preparing = async token => { entered.TrySetResult(); await proceed.Task.WaitAsync(token); };
            var start = runtime.Tunnels.StartAsync(selected, CancellationToken.None); await entered.Task;
            runtime.TunnelPolicy.Sites.Add(RoutingPlan.Create("latest.invalid", SiteAction.Block));
            proceed.TrySetResult(); await start;
            Assert(host.Requests.Single().Sites!.Any(r => r.Domain == "latest.invalid"));
            await runtime.StopTunnelAsync();
        });
        await check("Rules changed while routing lists are sent replace the pending configuration before Acquire", async () =>
        {
            var host = new TunnelHostFixture(); using var runtime = new ClientRuntime(new RecoveryCore(), host);
            runtime.TunnelPolicy.ProviderRouting = new("fixture", ["domain:old.invalid"], [], [], [], [], []);
            runtime.TunnelPolicy.UseProviderRouting = true;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Listing = async token => { if (host.Lists.Count == 1) { entered.TrySetResult(); await proceed.Task.WaitAsync(token); } };
            var start = runtime.Tunnels.StartAsync(selected, CancellationToken.None); await entered.Task;
            Assert(host.Requests.Count == 0);
            runtime.TunnelPolicy.Sites.Add(RoutingPlan.Create("fresh.invalid", SiteAction.Block));
            runtime.TunnelPolicy.ProviderRouting = new("fixture", ["domain:new.invalid"], [], [], [], [], []);
            proceed.TrySetResult(); await start;
            Assert(host.Lists.Count == 2 && host.Requests.Count == 1 && host.Requests[0].Sites!.Any(r => r.Domain == "fresh.invalid"));
            Assert(host.Lists[^1].Direct.Suffix.Contains("new.invalid") && !host.Lists[^1].Direct.Suffix.Contains("old.invalid"));
            await runtime.StopTunnelAsync();
        });
        await check("TUN cancellation after acquire releases protection and disposes helper subscriptions", async () =>
        {
            var host = new TunnelHostFixture(); var core = new RecoveryCore(); using var runtime = new ClientRuntime(core, host);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Dns = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
            using var cancel = new CancellationTokenSource();
            var start = runtime.Tunnels.StartAsync(selected, cancel.Token); await entered.Task;
            cancel.Cancel();
            try { await start; throw new InvalidOperationException("Expected cancellation"); } catch (OperationCanceledException) { }
            Assert(host.Calls.Contains("release") && host.Disposes == 1 && host.LastLink!.Fixture.Subscribers == 0);
            Assert(runtime.Tunnel is null && runtime.TunnelLink is null && !runtime.Tunnels.CanRebuild && core.State == ConnectionState.Idle);
        });
        await check("An old helper can omit routing lists, while acquisition failure still cleans up all resources", async () =>
        {
            var host = new TunnelHostFixture { FailLists = true }; using var runtime = new ClientRuntime(new RecoveryCore(), host);
            runtime.TunnelPolicy.ProviderRouting = new("fixture", ["domain:direct.invalid"], [], [], [], [], []);
            runtime.TunnelPolicy.UseProviderRouting = true;
            await runtime.Tunnels.StartAsync(selected, CancellationToken.None);
            Assert(host.Requests[^1].Lists is null && runtime.RoutingLists is null);
            await runtime.StopTunnelAsync(); host.FailAcquire = true;
            await ExpectUserErrorAsync(() => runtime.Tunnels.StartAsync(selected, CancellationToken.None));
            Assert(runtime.Tunnel is null && host.Disposes == 2 && host.LastLink!.Fixture.Subscribers == 0);
        });
        await check("Selective and foreign-session constraints are rebuilt from current application policy", async () =>
        {
            var host = new TunnelHostFixture(); using var runtime = new ClientRuntime(new RecoveryCore(), host);
            runtime.TunnelPolicy.Selective = true;
            runtime.TunnelPolicy.Programs.Add(ProgramRoutingPlan.Create("Discord.exe", SiteAction.Block));
            var foreign = selected with { Source = ProfileSource.Free };
            await runtime.Tunnels.StartAsync(foreign, CancellationToken.None);
            var request = host.Requests.Single();
            Assert(request.ForeignNode && request.DirectByDefault && runtime.Tunnels.ForeignSession);
            Assert(request.Programs!.First(p => p.Executable == "Discord.exe").Action == SiteAction.Block);
            Assert(ForeignGuard.With([], 100).All(rule => request.Sites!.Contains(rule)));
            await runtime.StopTunnelAsync();
        });
        await check("A foreign VPN or failed journal recovery prevents a new TUN acquisition", async () =>
        {
            var host = new TunnelHostFixture(); using var runtime = new ClientRuntime(new RecoveryCore(), host);
            await runtime.Tunnels.StartAsync(selected, CancellationToken.None);
            var releases = host.Calls.Count(c => c == "release"); host.Foreign = true;
            await ExpectUserErrorAsync(() => runtime.Tunnels.StartAsync(selected, CancellationToken.None));
            Assert(runtime.Tunnel?.BlocksBypass == true && host.Calls.Count(c => c == "release") == releases);
            host.Foreign = false; host.FailRecover = true;
            await ExpectUserErrorAsync(() => runtime.Tunnels.StartAsync(selected, CancellationToken.None));
            Assert(host.Connects == 1 && runtime.Tunnel is null);
        });
    }
}

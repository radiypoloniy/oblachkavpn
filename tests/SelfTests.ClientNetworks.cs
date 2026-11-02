namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientNetworkChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var profile = Profiles.Parse(link); var key = NetworkPolicy.Key(Guid.NewGuid());
        await check("Network auto-connect runs without a view and preserves manual Stop across a same-network reconnect", async () =>
        {
            var environment = new NetworkEnvironment { Current = (key, "Fixture", true) }; var core = new ConnectionCore();
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), networkEnvironment: environment);
            owner.Profiles.Imported = [profile]; owner.Profiles.Selected = profile; owner.Workspace.KnownNetworks[key] = new("Fixture", NetworkMode.Vpn);
            await owner.Networks.ProcessAsync(); Assert(core.State == ConnectionState.Running && core.Starts.Count == 1);
            await owner.Connections.StopAsync(); await owner.Networks.ProcessAsync();
            Assert(core.State == ConnectionState.Idle && core.Starts.Count == 1 && owner.Networks.StoppedOnNetwork == key);
            await owner.Connections.StartAsync(profile); Assert(owner.Networks.StoppedOnNetwork is null);
        });
        await check("Trusted networks and pause suppress automatic connection, unknown public networks obey the user's setting", async () =>
        {
            var environment = new NetworkEnvironment { Current = (key, "Fixture", true) }; var core = new ConnectionCore();
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), networkEnvironment: environment);
            owner.Profiles.Imported = [profile]; owner.Profiles.Selected = profile; owner.Preferences.SecureUnknown = true;
            owner.Workspace.KnownNetworks[key] = new("Fixture", NetworkMode.Trusted); await owner.Networks.ProcessAsync(); Assert(core.Starts.Count == 0);
            owner.Workspace.KnownNetworks.Clear(); owner.Preferences.SecureUnknown = false; await owner.Networks.ProcessAsync(); Assert(core.Starts.Count == 0);
            owner.Preferences.SecureUnknown = true; owner.Pause.SetDeadline(DateTimeOffset.UtcNow.AddMinutes(5));
            await owner.Networks.ProcessAsync(); Assert(core.Starts.Count == 0);
            owner.Pause.Cancel(); await owner.Networks.ProcessAsync(); Assert(core.Starts.Count == 1);
        });
        await check("A network transition waits for another operation before applying its rule set and connecting", async () =>
        {
            var environment = new NetworkEnvironment { Current = (key, "Fixture", false) }; var core = new ConnectionCore();
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), networkEnvironment: environment);
            owner.Profiles.Imported = [profile]; owner.Profiles.Selected = profile;
            owner.Workspace.RuleSets.Add(new("Work", [RoutingPlan.Create("example.org", SiteAction.Block)], []));
            owner.Workspace.KnownNetworks[key] = new("Fixture", NetworkMode.Vpn, "Work");
            using var busy = new CancellationTokenSource(); owner.Activity.Operation = busy;
            await owner.Networks.ProcessAsync(); Assert(core.Starts.Count == 0 && owner.Workspace.RuleSetName != "Work");
            owner.Activity.Operation = null; await owner.Networks.ProcessAsync();
            Assert(core.Starts.Count == 1 && owner.Workspace.RuleSetName == "Work" && owner.TunnelPolicy.Sites.Single().Action == SiteAction.Block);
        });
        await check("Following a changed physical network restarts only the core and retains the protected TUN session", async () =>
        {
            var environment = new NetworkEnvironment { Current = (key, "Fixture", false) }; var host = new TunnelHostFixture();
            using var owner = new ClientRuntime(new RecoveryCore { Working = profile }, host, networkEnvironment: environment);
            owner.Profiles.Imported = [profile]; owner.Preferences.SystemTunnel = true;
            await owner.Connections.StartAsync(profile); var session = owner.Tunnel;
            environment.HasMoved = true; await owner.Networks.ProcessAsync();
            Assert(ReferenceEquals(session, owner.Tunnel) && host.Connects == 1 && host.Disposes == 0 && owner.Tunnel?.BlocksBypass == true);
            await owner.Connections.StopAsync();
        });
        await check("A network rule set reaches the existing TUN without opening a newly added direct exception", async () =>
        {
            var host = new TunnelHostFixture(); using var owner = new ClientRuntime(new RecoveryCore { Working = profile }, host);
            owner.Profiles.Imported = [profile]; owner.Preferences.SystemTunnel = true;
            await owner.Connections.StartAsync(profile); var session = owner.Tunnel;
            owner.Workspace.RuleSets.Add(new("Work", [RoutingPlan.Create("example.org", SiteAction.Direct)], []));
            Assert(await owner.Rules.ApplySetAsync("Work"));
            Assert(ReferenceEquals(session, owner.Tunnel) && !owner.Tunnels.DirectAllowed && host.Calls.Contains("rules") && owner.Tunnel?.BlocksBypass == true);
            await owner.Connections.StopAsync();
        });
        await check("Manual Stop during network rule application prevents a later server-set reconnect", async () =>
        {
            var other = Profiles.Parse(link.Replace("example.com", "other.invalid").Replace("#Test", "#Other"));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new TunnelHostFixture { Ruling = _ => release.Task };
            using var owner = new ClientRuntime(new RecoveryCore { Working = profile }, host);
            owner.Profiles.Imported = [profile, other]; owner.Preferences.SystemTunnel = true;
            await owner.Connections.StartAsync(profile);
            owner.Workspace.RuleSets.Add(new("Work", [RoutingPlan.Create("example.org", SiteAction.Block)], [], [ServerMachines.Hash(other)]));
            var applying = owner.Rules.ApplySetAsync("Work"); await owner.Connections.StopAsync();
            release.SetResult(); await applying;
            Assert(owner.Engine.State == ConnectionState.Idle && host.Connects == 1 && owner.Activity.ManualStop);
        });
    }
    private sealed class NetworkEnvironment : IClientNetworkEnvironment
    {
        public (string Key, string Name, bool Public)? Current;
        public bool HasMoved;
        public (string Key, string Name, bool Public)? Read() => Current;
        public bool Moved(SystemTunnelSession session) { var moved = HasMoved; HasMoved = false; return moved; }
    }
}

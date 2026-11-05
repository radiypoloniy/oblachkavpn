namespace Oblachka;

public sealed class ClientTunnels
{
    private readonly ClientRuntime owner;
    private readonly IClientTunnelHost host;
    private readonly SemaphoreSlim transitions = new(1);
    public ClientTunnelSettings? Applied { get; private set; }
    public bool CanRebuild => Applied is not null;
    public SentRules SentRules { get; set; } = SentRules.None;
    public string? SentDirectDns { get; set; }
    internal CompiledRouting? SentLists { get; set; }
    internal string[] SentZapretDomains { get; set; } = [];
    public bool DirectAllowed { get; private set; }
    public bool ForeignSession { get; set; }
    internal ClientTunnels(ClientRuntime owner, IClientTunnelHost host) { this.owner = owner; this.host = host; }

    public async Task StartAsync(Profile selected, CancellationToken token)
    {
        await transitions.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested(); host.CheckForeign();
            try { await owner.StopTunnelAsync(); }
            catch (IOException) { /* Потерянный канал восстанавливается отдельным подключением ниже. */ }
            await host.RecoverIfNeededAsync(token);
            var plans = await host.PrepareAsync(Candidates(selected), token);
            token.ThrowIfCancellationRequested();
            try
            {
                var session = await owner.CreateTunnelAsync(token);
                var (settings, routing) = await ConfigureAsync(selected, session, token);
                token.ThrowIfCancellationRequested();
                await session.StartWithSparesAsync(plans, token, settings.Sites, settings.Programs, (owner.Engine as Engine)?.DirectEntry,
                    settings.DirectByDefault, owner.RoutingLists, settings.LocalNetwork, settings.QuicOverTcp, settings.CompatibleStack, settings.ForeignNode,
                    settings.ServiceRoutes, ServiceProfileAllowed);
                ForeignSession = settings.ForeignNode;
                DirectAllowed = settings.DirectByDefault || (owner.Engine as Engine)?.DirectEntry is not null ||
                    settings.Sites.Any(r => r.Action == SiteAction.Direct) || settings.Programs.Any(r => r.Action == SiteAction.Direct);
                SentRules = new(settings.Programs, settings.Sites); SentDirectDns = null;
                var domains = ClientTunnelPolicy.DirectDomains(routing, settings.Sites);
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(20));
                    if (await owner.TunnelLink!.SyncDnsAsync(domains, session.Stats, limit.Token)) SentDirectDns = string.Join(',', domains);
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is UserError or IOException or OperationCanceledException or TimeoutException) { }
                token.ThrowIfCancellationRequested();
                Applied = settings; SentZapretDomains = settings.ZapretDomains; owner.Profiles.Running = selected;
                owner.Workspace.WasConnected = true; owner.Persistence.Queue();
                owner.Monitor.ObserveProcess(); owner.Monitor.StartedFresh();
            }
            catch
            {
                try { await owner.StopTunnelAsync(); } catch (Exception ex) when (ex is UserError or IOException or TimeoutException or ObjectDisposedException) { }
                throw;
            }
        }
        finally { transitions.Release(); }
    }
    private async Task<(ClientTunnelSettings Settings, CompiledRouting? Routing)> ConfigureAsync(
        Profile selected, SystemTunnelSession session, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var settings = owner.TunnelPolicy.Snapshot(selected);
            ForeignSession = settings.ForeignNode;
            var routing = CompiledRouting.Compile(settings.Routing, GeoStore.Default);
            if (owner.Engine is Engine core)
            {
                core.SiteRules = []; core.Physical = true; core.Fragment = settings.Fragment; core.FragmentShape = settings.FragmentShape;
                core.Routing = routing;
                core.DirectEntry = routing?.HasDirect == true || settings.Zapret ? CoreDirectEntry.Create(Engine.FreePort()) : null;
            }
            session.Tuning = settings.Tuning; owner.RoutingLists = null; SentLists = null;
            if (routing is { IsEmpty: false })
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(60));
                    await owner.TunnelLink!.SendListsAsync(routing, limit.Token);
                    owner.RoutingLists = new(routing.Order, routing.GlobalProxy);
                    SentLists = routing;
                    if (owner.Engine is Engine shared) shared.Routing = null;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && (ex is UserError or IOException or OperationCanceledException or TimeoutException)) { }
            }
            token.ThrowIfCancellationRequested();
            // Только при запуске: UI мог изменить правила во время UAC или отправки списков.
            if (System.Text.Json.JsonSerializer.Serialize(settings) == System.Text.Json.JsonSerializer.Serialize(owner.TunnelPolicy.Snapshot(selected)))
                return (settings, routing);
        }
        throw new UserError("Настройки меняются во время запуска. Повторите подключение после изменения правил.");
    }
    public Profile[] Candidates(Profile selected)
    {
        var ordinary = OrdinaryCandidates(selected);
        var services = owner.TunnelPolicy.ServiceRoutes;
        if (selected.IsFree || services.Count == 0) return ordinary;
        ServiceRoutePlan.Validate(services);
        var available = owner.Profiles.Imported.Where(ServiceProfileAllowed)
            .DistinctBy(ClientServerCatalog.Id).ToDictionary(p => ClientServerCatalog.Id(p)!, StringComparer.OrdinalIgnoreCase);
        var reserved = services.Select(s => available.GetValueOrDefault(s.ProfileId)).OfType<Profile>();
        return new[] { selected }.Concat(reserved).Concat(ordinary).DistinctBy(ProfileSelection.Identity).ToArray();
    }

    internal bool ServiceProfileAllowed(Profile profile) => !profile.IsFree &&
        (owner.Profiles.RuleSetServers is not { } servers || servers.Contains(ServerMachines.Hash(profile)));

    private Profile[] OrdinaryCandidates(Profile selected)
    {
        if (selected.IsFree) return [selected];
        var data = owner.Profiles; var profiles = data.Imported.Where(p => !p.IsFree).ToArray();
        if (data.AutoSelect)
            return new[] { selected }.Concat(RouteCandidates.ByLatency(RouteCandidates.For(profiles, selected, anyNode: true, data.InAutoPool), profiles, data.LatestMeasurement)
                .Select(i => profiles[i])).Concat(profiles).DistinctBy(ProfileSelection.Identity).ToArray();
        var candidates = RouteCandidates.For(profiles, selected, pool: data.InAutoPool, machine: data.MachineMode);
        if (candidates.Length == 0) return [selected];
        return RouteSearch.Order(candidates.Length, RouteCandidates.StartAt(candidates, profiles, selected)).Select(i => profiles[candidates[i]])
            .Concat(candidates.Select(i => profiles[i])).Concat(profiles.Where(p => p.IsService)).DistinctBy(ProfileSelection.Identity).ToArray();
    }
}

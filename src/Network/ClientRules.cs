namespace Oblachka;

// Применение правил не держит представление. Сессия проверяется после каждого ожидания.
public sealed class ClientRules(ClientRuntime owner) : IDisposable
{
    private readonly SemaphoreSlim ruleSync = new(1), dnsSync = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private bool disposed;
    public string? Problem { get; set; }
    public SentRules? PendingBefore { get; set; }
    public event Action? Changed;
    private bool Active => !disposed && owner.Tunnel is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) };
    public void Remember() { Problem = null; PendingBefore = null; }
    public async Task<bool> SyncDnsAsync(IReadOnlyList<SiteRule>? sites = null)
    {
        if (disposed) return false;
        await dnsSync.WaitAsync();
        try
        {
            if (!Active || owner.TunnelLink is not { } link) return false;
            var session = owner.Tunnel;
            var routing = (owner.Engine as Engine)?.Routing ?? (owner.RoutingLists is not null ? CompiledRouting.Compile(owner.TunnelPolicy.RoutingProfiles(), GeoStore.Default) : null);
            var domains = ClientTunnelPolicy.DirectDomains(routing, sites ?? owner.TunnelPolicy.Sites);
            var key = string.Join(',', domains);
            if (key == owner.Tunnels.SentDirectDns) return true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            if (!await link.SyncDnsAsync(domains, session?.Stats, timeout.Token) || !ReferenceEquals(session, owner.Tunnel) || !ReferenceEquals(link, owner.TunnelLink) || !Active) return false;
            owner.Tunnels.SentDirectDns = key; return true;
        }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or TimeoutException) { return false; }
        finally { dnsSync.Release(); }
    }
    public async Task SyncAsync()
    {
        if (disposed) return;
        await ruleSync.WaitAsync();
        try
        {
            if (!Active || owner.TunnelLink is not { } link) return;
            var session = owner.Tunnel;
            var next = new SentRules(owner.TunnelPolicy.LeasePrograms(), owner.TunnelPolicy.LeaseSites(owner.Tunnels.ForeignSession));
            var dnsReady = await SyncDnsAsync(next.Sites);
            if (!ReferenceEquals(link, owner.TunnelLink) || !ReferenceEquals(session, owner.Tunnel) || !Active) return;
            if (next.Key == owner.Tunnels.SentRules.Key && Problem is null && dnsReady) return;
            PendingBefore ??= owner.Tunnels.SentRules; var before = PendingBefore;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await link.SendRulesAsync(next, timeout.Token);
            if (!ReferenceEquals(link, owner.TunnelLink) || !ReferenceEquals(session, owner.Tunnel) || !Active) return;
            owner.Tunnels.SentRules = next; var cacheReady = true;
            if (session?.Stats is { } stats)
            {
                await Task.Delay(400, timeout.Token);
                if (!ReferenceEquals(session, owner.Tunnel) || !Active) return;
                cacheReady = await CoreDnsCache.FlushAsync(stats, timeout.Token);
                await CoreConnections.CloseChangedAsync(stats, before, next, timeout.Token); Changed?.Invoke();
                await Task.Delay(1100, timeout.Token);
                if (!ReferenceEquals(session, owner.Tunnel) || !Active) return;
                cacheReady = await CoreDnsCache.FlushAsync(stats, timeout.Token) && cacheReady;
                if (await CoreConnections.CloseMismatchedAsync(stats, before, next, timeout.Token) > 0) Changed?.Invoke();
            }
            Problem = dnsReady && cacheReady ? null : "Маршруты применены, но обновление DNS не завершилось. Повторите применение или переподключите VPN.";
            if (Problem is null) PendingBefore = null;
            if (!disposed) Changed?.Invoke();
        }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
        { if (!disposed) { Problem = "Правила сохранены, но подключение их не приняло. Повторите применение или переподключите VPN."; Changed?.Invoke(); } }
        finally { ruleSync.Release(); }
    }
    public async Task<bool> ApplySetAsync(string name)
    {
        if (disposed) return false;
        var workspace = owner.Workspace;
        if (name == workspace.RuleSetName) return false;
        var all = workspace.SavedRuleSets(owner); var next = all.FirstOrDefault(s => s.Name == name);
        if (next is null) return false;
        RoutingPlan.Validate(next.Sites); ProgramRoutingPlan.Validate(next.Programs);
        workspace.RuleSets.Clear(); workspace.RuleSets.AddRange(all); workspace.RuleSetName = name;
        owner.TunnelPolicy.Sites.Clear(); owner.TunnelPolicy.Sites.AddRange(next.Sites);
        owner.TunnelPolicy.Programs.Clear(); owner.TunnelPolicy.Programs.AddRange(next.Programs);
        owner.Profiles.RuleSetServers = next.Servers?.Where(s => s is { Length: 16 } && s.All(char.IsAsciiHexDigit)).Take(Subscriptions.MaxProfiles).ToHashSet();
        owner.Persistence.Queue(); await SyncAsync(); if (disposed) return false;
        if (owner.Profiles.Selected is { } current && owner.Profiles.RuleSetServers is { } servers && !servers.Contains(ServerMachines.Hash(current)))
        {
            var allowed = owner.Profiles.Imported.Where(p => !p.IsFree && servers.Contains(ServerMachines.Hash(p))).ToArray();
            var measured = allowed.Select(owner.Profiles.LatestMeasurement).OfType<ServerMeasurement>().GroupBy(m => m.Fingerprint).ToDictionary(g => g.Key, g => g.First());
            if (allowed.Length > 0)
            {
                var best = ServerMeasurements.Best(allowed, measured, DateTimeOffset.UtcNow) is int index ? allowed[index] : allowed[0];
                if (!owner.Profiles.AutoSelect)
                {
                    var members = owner.Profiles.Imported.Where(p => ServerMachines.Key(p) == ServerMachines.Key(best)).ToArray();
                    owner.Profiles.MachineMode = members.Length > 1;
                    if (members.Length > 1) best = owner.Selection.BestMachine(members);
                }
                var active = owner.Engine.State is not ConnectionState.Idle || owner.Tunnel is not null;
                if (owner.Activity.ManualStop) { owner.Profiles.Selected = best; owner.Persistence.Queue(); }
                else if (!active || owner.Tunnel is null || owner.Tunnel.CanSwitchTo(best)) await owner.Connections.SelectAsync(best);
                else { await owner.Connections.StopAsync(); await owner.Connections.StartAsync(best); }
            }
        }
        if (!disposed) Changed?.Invoke(); return true;
    }
    public void Dispose() { if (disposed) return; disposed = true; shutdown.Cancel(); Changed = null; }
}

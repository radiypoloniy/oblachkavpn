namespace Oblachka;

internal interface IClientTunnelLink : IAsyncDisposable
{
    INetworkHelperClient Client { get; }
    IHelperLifetime? Lifetime { get; }
    Task SendListsAsync(CompiledRouting routing, CancellationToken token);
    Task<bool> SyncDnsAsync(string[] domains, CoreStatsEntry? stats, CancellationToken token);
}
internal interface IClientTunnelHost
{
    void CheckForeign();
    bool IsService { get; }
    Task RecoverIfNeededAsync(CancellationToken token);
    Task<IReadOnlyList<TunnelEndpointPlan>> PrepareAsync(IReadOnlyList<Profile> profiles, CancellationToken token);
    Task<IClientTunnelLink> ConnectAsync(CancellationToken token);
}
internal sealed class ClientTunnelHost : IClientTunnelHost
{
    public bool IsService => NetworkHelperConnection.ServiceRunning;
    public void CheckForeign() { if (ForeignTunnel.Find() is { } foreign) throw new UserError(ForeignTunnel.Explain(foreign)); }
    public async Task RecoverIfNeededAsync(CancellationToken token)
    {
        if (IsService)
        {
            if (NetworkSessionJournal.PeekState(NetworkService.JournalPath) is not { } state || state == NetworkSessionState.Released) return;
            await using var helper = await NetworkHelperConnection.ConnectAsync(token);
            await helper.Client.RecoverAsync(token);
        }
        else if (NetworkSessionJournal.PeekState(NetworkHelperHost.JournalPath) is { } state && state != NetworkSessionState.Released)
        {
            await using var helper = await HelperProcessLifetime.ConnectAsync(NetworkHelperLauncher.Launch, token);
            if (NetworkSessionJournal.PeekState(NetworkHelperHost.JournalPath) is { } remaining && remaining != NetworkSessionState.Released)
                throw new UserError("Сеть после предыдущей сессии не восстановлена. Восстановите сеть и подключитесь снова.");
        }
    }
    public Task<IReadOnlyList<TunnelEndpointPlan>> PrepareAsync(IReadOnlyList<Profile> profiles, CancellationToken token) => TunnelEndpoints.PrepareSetAsync(profiles, token);
    public async Task<IClientTunnelLink> ConnectAsync(CancellationToken token) => new Link(await NetworkHelperConnection.ConnectAsync(token));
    private sealed class Link(IHelperLifetime helper) : IClientTunnelLink
    {
        public INetworkHelperClient Client => helper.Client;
        public IHelperLifetime Lifetime => helper;
        public Task SendListsAsync(CompiledRouting routing, CancellationToken token) => helper.Client.RoutingListsAsync(routing, token);
        public async Task<bool> SyncDnsAsync(string[] domains, CoreStatsEntry? stats, CancellationToken token)
        {
            await helper.Client.DirectDnsAsync(domains, token);
            await Task.Delay(400, token);
            return stats is null || await CoreDnsCache.FlushAsync(stats, token);
        }
        public ValueTask DisposeAsync() => helper.DisposeAsync();
    }
}

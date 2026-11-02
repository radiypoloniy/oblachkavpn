namespace Oblachka;

public sealed partial class ClientConnections
{
    private async Task StartOneAsync(Profile target, bool allowReconnect, CancellationToken token)
    {
        var mode = owner.Preferences.SystemTunnel;
        var session = owner.Tunnel;
        var runningTunnel = session is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) };
        if (!mode && session is not null) { await owner.StopTunnelAsync(); runningTunnel = false; }
        if (mode && runningTunnel && !session!.CanSwitchTo(target))
        {
            if (!allowReconnect) throw new UserError("Подтвердите переподключение со снятием защиты.");
            await owner.StopTunnelAsync(); runningTunnel = false;
        }
        if (owner.Engine is Engine core)
        {
            if (owner.TunnelPolicy.Advanced.ProxyPort is { } port) core.PreferredPort = port;
            Utls.Default = owner.TunnelPolicy.Advanced.Utls;
            TunnelHealth.Custom = owner.TunnelPolicy.Advanced.ProbeUrl is { } probe ? new Uri(probe) : null;
        }
        // Tunnels сам строит актуальную конфигурацию после подготовки: не компилируем геобазы дважды.
        if (mode && !runningTunnel)
        {
            if (owner.Tunnel is null && owner.Engine.State is not ConnectionState.Idle) owner.Engine.Stop();
            await owner.Tunnels.StartAsync(target, token); return;
        }
        var settings = owner.TunnelPolicy.Snapshot(target);
        var routing = CompiledRouting.Compile(settings.Routing, GeoStore.Default);
        if (owner.Engine is Engine configured)
        {
            configured.SiteRules = mode ? [] : settings.Sites;
            configured.Fragment = settings.Fragment; configured.FragmentShape = settings.FragmentShape;
            configured.Routing = runningTunnel && owner.RoutingLists is not null ? null : routing;
            configured.Physical = mode;
            if (!runningTunnel) configured.DirectEntry = null;
        }
        if (mode && runningTunnel)
        {
            await session!.SwitchAsync(target, token);
            if (owner.TunnelLink is { } link)
            {
                var domains = ClientTunnelPolicy.DirectDomains(routing, settings.Sites);
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(20));
                    owner.Tunnels.SentDirectDns = await link.SyncDnsAsync(domains, session.Stats, limit.Token) ? string.Join(',', domains) : null;
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is UserError or IOException or OperationCanceledException or TimeoutException)
                { owner.Tunnels.SentDirectDns = null; }
            }
        }
        else
        {
            if (owner.Engine.State is not ConnectionState.Idle) owner.Engine.Stop();
            await owner.Engine.StartAsync(target, token);
        }
    }
}

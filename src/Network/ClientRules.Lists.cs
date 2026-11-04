namespace Oblachka;

internal enum RoutingListsResult { Inactive, Deferred, Applied, Reconnected, Failed }

public sealed partial class ClientRules
{
    private readonly SemaphoreSlim listSync = new(1);
    private bool TunnelActive => owner.Tunnel is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) };
    private bool BusyForLists => owner.Activity.Operation is not null || owner.Activity.VmTestActive ||
        owner.Connections.IsActive || owner.Connections.IsStopping;

    internal async Task<RoutingListsResult> ApplyListsAsync()
    {
        if (disposed) return RoutingListsResult.Inactive;
        await listSync.WaitAsync();
        try
        {
            if (disposed || owner.Activity.ManualStop || !TunnelActive && owner.Engine.State == ConnectionState.Idle)
                return RoutingListsResult.Inactive;
            if (BusyForLists) return RoutingListsResult.Deferred;
            owner.Activity.RecoveryWait?.Cancel();
            var routing = CompiledRouting.Compile(owner.TunnelPolicy.RoutingProfiles(), GeoStore.Default);
            var session = owner.Tunnel;
            if (await UpdateListsAsync(routing) is { } hotResult) return hotResult;
            if (disposed || owner.Activity.ManualStop || !ReferenceEquals(session, owner.Tunnel) || BusyForLists)
                return RoutingListsResult.Deferred;
            if (owner.Profiles.Selected is not { } target) return RoutingListsResult.Inactive;

            // Порядок, итог и разрешения прямого выхода зафиксированы в аренде. Если списки
            // исполняет ядро прокси, его можно перезапустить внутри действующей защиты.
            var needsNewLease = TunnelActive && (owner.RoutingLists is not null ||
                (owner.Engine as Engine)?.DirectEntry is null && (routing?.HasDirect == true || owner.TunnelPolicy.Zapret));
            await owner.Connections.RestartAsync(target, rebuildTunnel: needsNewLease);
            return session is not null && ReferenceEquals(session, owner.Tunnel)
                ? RoutingListsResult.Applied : RoutingListsResult.Reconnected;
        }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
        { return RoutingListsResult.Failed; }
        finally { listSync.Release(); }
    }

    private async Task<RoutingListsResult?> UpdateListsAsync(CompiledRouting? routing)
    {
        if (!TunnelActive || owner.Tunnel is not { } session || owner.TunnelLink is not { } link ||
            owner.RoutingLists is not { } lists) return null;
        routing ??= new CompiledRouting(new(), new(), new(), lists.GlobalProxy, lists.Order, []);
        if (!routing.Order.SequenceEqual(lists.Order) || routing.GlobalProxy != lists.GlobalProxy ||
            routing.HasDirect && (owner.Engine as Engine)?.DirectEntry is null) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await link.SendListsAsync(routing, timeout.Token);
            if (disposed || !ReferenceEquals(session, owner.Tunnel) || !ReferenceEquals(link, owner.TunnelLink))
                return RoutingListsResult.Deferred;
            owner.Tunnels.SentLists = routing;
            owner.Tunnels.SentDirectDns = null;
            _ = SyncDnsAsync();
            return RoutingListsResult.Applied;
        }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
        { return RoutingListsResult.Failed; }
    }
}

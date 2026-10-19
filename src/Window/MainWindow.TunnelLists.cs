namespace Oblachka;

// Списки маршрутизации в ядре туннеля (RoutingLists). В «Весь компьютер» списки уходят в службу до
// запуска сессии, ядро окна их больше не исполняет и трафик не несёт: оно остаётся для проверок
// связи и входа прямого трафика (сайт-чек, zapret). Тогда работает и одно ядро (DirectOutbound) —
// «Российские сайты напрямую» включены почти у всех, и без этого выигрыш до них не доходил.
// Старая служба без команды RoutingLists — списки по-прежнему в ядре окна.
public partial class MainWindow
{
    // Порядок и итог списков этой сессии; null — списки в ядре окна или их нет.
    private RoutingListsEntry? tunnelLists;

    private bool TunnelListsActive => tunnelLists is not null && SystemTunnelActive;

    // До запуска сессии: отдать списки службе. Удалось — ядро окна идёт без списков.
    private async Task<RoutingListsEntry?> SendTunnelListsAsync(NetworkHelperClient client)
    {
        tunnelLists = null;
        if (engine is not Engine core || CurrentRouting() is not { IsEmpty: false } routing) return null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await client.RoutingListsAsync(routing, timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is UserError or IOException or OperationCanceledException or TimeoutException) { return null; }
        core.Routing = null;
        return tunnelLists = new(routing.Order, routing.GlobalProxy);
    }

    // Списки изменились под работающей сессией: новые файлы, без переподключения и без снятия защиты.
    // false — так нельзя (другой порядок или итог, нужен прямой выход, которого нет у аренды).
    private async Task<bool> UpdateTunnelListsAsync()
    {
        if (!TunnelListsActive || helperLifetime is not { } lifetime || tunnelLists is not { } lists) return false;
        var routing = CurrentRouting() ?? new CompiledRouting(new(), new(), new(), lists.GlobalProxy, lists.Order, []);
        if (!routing.Order.SequenceEqual(lists.Order) || routing.GlobalProxy != lists.GlobalProxy || routing.HasDirect && !HasDirectEntry) return false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await lifetime.Client.RoutingListsAsync(routing, timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is UserError or IOException or OperationCanceledException or TimeoutException) { return false; }
        coreZapretKey = DesiredZapretKey;
        sentDirectDns = ""; _ = SyncDirectDnsAsync();
        return true;
    }
}

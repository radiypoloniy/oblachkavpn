namespace Oblachka;

// Список сайтов, которые идут мимо VPN, для DNS ядра туннеля (DirectDns): их имена
// разрешает DNS физической сети. Отправляется после каждого запуска ядра под «Весь
// компьютер» — маршруты ядра и DNS туннеля меняются вместе, без переподключения.
public partial class MainWindow
{
    private string sentDirectDns = "";

    private static string[] DirectDomains(CompiledRouting? routing, IEnumerable<SiteRule> rules)
    {
        // «Всё напрямую, кроме списка» — DNS туннеля нужен как раз списку VPN; такого режима
        // здесь нет, и имена разрешаются через туннель, как раньше.
        if (routing is { GlobalProxy: false }) return [];
        var names = (routing?.Direct.Suffix ?? []).Concat(routing?.Direct.Full ?? [])
            .Concat(rules.Where(r => r.Action == SiteAction.Direct).Select(r => r.Domain));
        return names.Select(n => n.Trim().TrimStart('.').ToLowerInvariant()).Where(DirectDnsRequest.Valid)
            .Distinct(StringComparer.Ordinal).Take(DirectDnsRequest.MaxParts * DirectDnsRequest.MaxPerPart).ToArray();
    }

    private async Task SyncDirectDnsAsync()
    {
        if (helperLifetime is not { } lifetime || !SystemTunnelActive || engine is not Engine core) return;
        var domains = DirectDomains(core.Routing, siteRules);
        var key = string.Join(',', domains);
        if (key == sentDirectDns) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await lifetime.Client.DirectDnsAsync(domains, timeout.Token);
            sentDirectDns = key;
        }
        // Старая служба без этой команды или сбой канала: имена просто остаются через туннель.
        catch (Exception ex) when (ex is UserError or IOException or OperationCanceledException or TimeoutException) { }
    }
}

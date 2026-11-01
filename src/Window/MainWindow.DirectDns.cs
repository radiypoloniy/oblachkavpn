namespace Oblachka;

// Список сайтов, которые идут мимо VPN, для DNS ядра туннеля (DirectDns): их имена
// разрешает DNS физической сети. Отправляется после каждого запуска ядра под «Весь
// компьютер» — маршруты ядра и DNS туннеля меняются вместе, без переподключения.
public partial class MainWindow
{
    private string? sentDirectDns;
    private readonly SemaphoreSlim directDnsSync = new(1);

    private static string[] DirectDomains(CompiledRouting? routing, IEnumerable<SiteRule> rules)
    {
        // «Всё напрямую, кроме списка» — DNS туннеля нужен как раз списку VPN; такого режима
        // здесь нет, и имена разрешаются через туннель, как раньше.
        if (routing is { GlobalProxy: false }) return [];
        var names = rules.Where(r => r.Action == SiteAction.Direct && SiteTarget.Kind(r.Domain) == SiteTargetKind.Domain).Select(r => r.Domain)
            .Concat(routing?.Direct.Suffix ?? []).Concat(routing?.Direct.Full ?? []);
        return names.Select(n => n.Trim().TrimStart('.').ToLowerInvariant()).Where(DirectDnsRequest.Valid)
            .Distinct(StringComparer.Ordinal).Take(DirectDnsRequest.MaxParts * DirectDnsRequest.MaxPerPart).ToArray();
    }

    private async Task<bool> SyncDirectDnsAsync(IReadOnlyList<SiteRule>? sites = null)
    {
        await directDnsSync.WaitAsync();
        try
        {
            if (helperLifetime is not { } lifetime || !SystemTunnelActive || engine is not Engine core) return false;
            var session = systemTunnel;
            var domains = DirectDomains(core.Routing ?? (TunnelListsActive ? CurrentRouting() : null), sites ?? siteRules);
            var key = string.Join(',', domains);
            if (key == sentDirectDns) return true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await lifetime.Client.DirectDnsAsync(domains, timeout.Token);
            // Файл перечитывается асинхронно; прежний ответ DoH не должен остаться в кэше.
            await Task.Delay(400, timeout.Token);
            if (!ReferenceEquals(lifetime, helperLifetime) || !ReferenceEquals(session, systemTunnel) || !SystemTunnelActive) return false;
            if (session?.Stats is { } stats && !await CoreDnsCache.FlushAsync(stats, timeout.Token)) return false;
            sentDirectDns = key;
            return true;
        }
        catch (Exception ex) when (ex is UserError or IOException or OperationCanceledException or TimeoutException) { return false; }
        finally { directDnsSync.Release(); }
    }
}

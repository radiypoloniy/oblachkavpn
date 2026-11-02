namespace Oblachka;

// Список сайтов, которые идут мимо VPN, для DNS ядра туннеля (DirectDns): их имена
// разрешает DNS физической сети. Отправляется после каждого запуска ядра под «Весь
// компьютер» — маршруты ядра и DNS туннеля меняются вместе, без переподключения.
public partial class MainWindow
{
    private string? sentDirectDns { get => runtime.Tunnels.SentDirectDns; set => runtime.Tunnels.SentDirectDns = value; }

    private static string[] DirectDomains(CompiledRouting? routing, IEnumerable<SiteRule> rules) => ClientTunnelPolicy.DirectDomains(routing, rules);

    private Task<bool> SyncDirectDnsAsync(IReadOnlyList<SiteRule>? sites = null) => runtime.Rules.SyncDnsAsync(sites);
}

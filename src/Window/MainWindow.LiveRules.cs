namespace Oblachka;

// Правила программ на ходу: при «Весь компьютер» любое изменение сразу уходит в службу
// (ProgramRuleSets), ядро туннеля перечитывает файлы правил — без переподключения и без снятия
// защиты. Прямой выход защита разрешает, только если «напрямую» было при подключении: правило
// «напрямую», появившееся позже, до переподключения блокирует программу, а не пускает её мимо.
public partial class MainWindow
{
    private bool tunnelDirectAllowed => runtime.Tunnels.DirectAllowed;
    // Правила программ и сайтов, что сейчас у ядра туннеля.
    private SentRules sentRules { get => runtime.Tunnels.SentRules; set => runtime.Tunnels.SentRules = value; }
    private readonly SemaphoreSlim ruleSync = new(1);

    // Вызывается при запуске сессии: то же условие, что у ValidatedNetworkLease.HasDirect.
    private void RememberTunnelDirect()
    {
        ruleApplyProblem = null; pendingRulesBefore = null;
    }

    private void ProgramRulesChanged()
    {
        RefreshRuleApplyStatus();
        if (!SystemTunnelActive) return;
        _ = SyncProgramRulesAsync();
        if (!tunnelDirectAllowed && (programRules.Any(r => r.Action == SiteAction.Direct) || siteRules.Any(r => r.Action == SiteAction.Direct)))
            DetailText.Text = "Правило «напрямую» заработает после переподключения: до него программа или сайт заблокированы, а не идут мимо VPN.";
    }

    private async Task SyncProgramRulesAsync()
    {
        await ruleSync.WaitAsync();
        try
        {
            if (closed || helperLifetime is not { } lifetime || !SystemTunnelActive) return;
            var session = systemTunnel;
            var next = new SentRules(LeasePrograms().ToArray(), LeaseSites());
            var dnsReady = await SyncDirectDnsAsync(next.Sites);
            if (!ReferenceEquals(lifetime, helperLifetime) || !ReferenceEquals(session, systemTunnel) || !SystemTunnelActive) return;
            if (next.Key == sentRules.Key && ruleApplyProblem is null && dnsReady) return;
            pendingRulesBefore ??= sentRules;
            var before = pendingRulesBefore;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await lifetime.Client.ProgramRulesAsync(next.Programs, next.Sites, timeout.Token);
            if (!ReferenceEquals(lifetime, helperLifetime) || !ReferenceEquals(session, systemTunnel) || !SystemTunnelActive) return;
            sentRules = next;
            var cacheReady = true;
            if (session?.Stats is { } stats)
            {
                await Task.Delay(400, timeout.Token);
                if (!ReferenceEquals(session, systemTunnel) || !SystemTunnelActive) return;
                cacheReady = await CoreDnsCache.FlushAsync(stats, timeout.Token);
                await CoreConnections.CloseChangedAsync(stats, before, next, timeout.Token);
                _ = RefreshConnectionsAsync();
                // Повторно закрываем только соединения, успевшие открыться по прежнему правилу.
                await Task.Delay(1100, timeout.Token);
                if (!ReferenceEquals(session, systemTunnel) || !SystemTunnelActive) return;
                cacheReady = await CoreDnsCache.FlushAsync(stats, timeout.Token) && cacheReady;
                if (await CoreConnections.CloseMismatchedAsync(stats, before, next, timeout.Token) > 0) _ = RefreshConnectionsAsync();
            }
            ruleApplyProblem = dnsReady && cacheReady ? null : "Маршруты применены, но обновление DNS не завершилось. Повторите применение или переподключите VPN.";
            if (ruleApplyProblem is null) pendingRulesBefore = null;
            RefreshRuleApplyStatus();
        }
        catch (Exception ex) when (ex is UserError or IOException or OperationCanceledException or TimeoutException)
        {
            if (!closed)
            {
                ruleApplyProblem = "Правила сохранены, но подключение их не приняло. Повторите применение или переподключите VPN.";
                DetailText.Text = ruleApplyProblem; RefreshRuleApplyStatus();
            }
        }
        finally { ruleSync.Release(); }
    }
}

namespace Oblachka;

// Правила программ на ходу: при «Весь компьютер» любое изменение сразу уходит в службу
// (ProgramRuleSets), ядро туннеля перечитывает файлы правил — без переподключения и без снятия
// защиты. Прямой выход защита разрешает, только если «напрямую» было при подключении: правило
// «напрямую», появившееся позже, до переподключения блокирует программу, а не пускает её мимо.
public partial class MainWindow
{
    private bool tunnelDirectAllowed;
    private string sentProgramRules = "";
    private ProgramRule[] sentRules = [];

    // Вызывается при запуске сессии: то же условие, что у ValidatedNetworkLease.HasDirect.
    private void RememberTunnelDirect() =>
        tunnelDirectAllowed = selectiveVpn || (engine as Engine)?.DirectEntry is not null || siteRules.Any(r => r.Action == SiteAction.Direct) || programRules.Any(r => r.Action == SiteAction.Direct);

    private void ProgramRulesChanged()
    {
        if (!SystemTunnelActive) return;
        _ = SyncProgramRulesAsync();
        if (!tunnelDirectAllowed && programRules.Any(r => r.Action == SiteAction.Direct))
            DetailText.Text = "Правило «напрямую» заработает после переподключения: до него программа заблокирована, а не идёт мимо VPN.";
    }

    private async Task SyncProgramRulesAsync()
    {
        if (helperLifetime is not { } lifetime || !SystemTunnelActive) return;
        var rules = LeasePrograms();
        var key = string.Join('|', rules.Select(r => r.Key + ">" + r.Action));
        if (key == sentProgramRules) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await lifetime.Client.ProgramRulesAsync(rules, timeout.Token);
            sentProgramRules = key;
            var before = sentRules; sentRules = rules.ToArray();
            // Ядро перечитывает файлы правил не мгновенно: открытые соединения закрываем, когда
            // новые правила уже действуют, иначе программа переподключилась бы прежним путём.
            if (systemTunnel?.Stats is { } stats)
            {
                await Task.Delay(1500);
                await CoreConnections.CloseChangedAsync(stats, before, sentRules, CancellationToken.None);
            }
        }
        // Старая служба без этой команды или сбой канала: правила вступят в силу при следующем подключении.
        catch (Exception ex) when (ex is UserError or IOException or OperationCanceledException or TimeoutException)
        {
            if (!closed) DetailText.Text = "Правила программ вступят в силу при следующем подключении.";
        }
    }
}

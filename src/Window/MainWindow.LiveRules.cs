namespace Oblachka;

// Правила программ на ходу: при «Весь компьютер» любое изменение сразу уходит в службу
// (ProgramRuleSets), ядро туннеля перечитывает файлы правил — без переподключения и без снятия
// защиты. Прямой выход защита разрешает, только если «напрямую» было при подключении: правило
// «напрямую», появившееся позже, до переподключения блокирует программу, а не пускает её мимо.
public partial class MainWindow
{
    private bool tunnelDirectAllowed;
    // Правила программ и сайтов, что сейчас у ядра туннеля.
    private SentRules sentRules = SentRules.None;

    // Вызывается при запуске сессии: то же условие, что у ValidatedNetworkLease.HasDirect.
    private void RememberTunnelDirect() =>
        tunnelDirectAllowed = selectiveVpn || (engine as Engine)?.DirectEntry is not null || siteRules.Any(r => r.Action == SiteAction.Direct) || programRules.Any(r => r.Action == SiteAction.Direct) || ServiceDirectApps.Length > 0;

    private void ProgramRulesChanged()
    {
        if (!SystemTunnelActive) return;
        _ = SyncProgramRulesAsync();
        if (!tunnelDirectAllowed && (programRules.Any(r => r.Action == SiteAction.Direct) || siteRules.Any(r => r.Action == SiteAction.Direct)))
            DetailText.Text = "Правило «напрямую» заработает после переподключения: до него программа или сайт заблокированы, а не идут мимо VPN.";
    }

    private async Task SyncProgramRulesAsync()
    {
        if (helperLifetime is not { } lifetime || !SystemTunnelActive) return;
        var next = new SentRules(LeasePrograms().ToArray(), LeaseSites());
        if (next.Key == sentRules.Key) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await lifetime.Client.ProgramRulesAsync(next.Programs, next.Sites, timeout.Token);
            var before = sentRules; sentRules = next;
            // Ядро перечитывает файлы правил не мгновенно. Первый проход — почти сразу: обычно ядро уже
            // успело, и программа переподключается по новому правилу без заметной паузы. Второй —
            // через секунду, только для соединений, что всё ещё идут не туда (переподключились раньше,
            // чем ядро перечитало правила). Свежие соединения по новому правилу он не трогает.
            if (systemTunnel?.Stats is { } stats)
            {
                var after = sentRules;
                await Task.Delay(400);
                await CoreConnections.CloseChangedAsync(stats, before, after, CancellationToken.None);
                _ = RefreshConnectionsAsync();
                await Task.Delay(1100);
                if (await CoreConnections.CloseMismatchedAsync(stats, before, after, CancellationToken.None) > 0) _ = RefreshConnectionsAsync();
            }
        }
        // Старая служба без этой команды или сбой канала: правила вступят в силу при следующем подключении.
        catch (Exception ex) when (ex is UserError or IOException or OperationCanceledException or TimeoutException)
        {
            if (!closed) DetailText.Text = "Правила программ вступят в силу при следующем подключении.";
        }
    }
}

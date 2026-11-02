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

    // Вызывается при запуске сессии: то же условие, что у ValidatedNetworkLease.HasDirect.
    private void RememberTunnelDirect()
    {
        runtime.Rules.Remember();
    }

    private void ProgramRulesChanged()
    {
        RefreshRuleApplyStatus();
        if (!SystemTunnelActive) return;
        _ = SyncProgramRulesAsync();
        if (!tunnelDirectAllowed && (programRules.Any(r => r.Action == SiteAction.Direct) || siteRules.Any(r => r.Action == SiteAction.Direct)))
            DetailText.Text = "Правило «напрямую» заработает после переподключения: до него программа или сайт заблокированы, а не идут мимо VPN.";
    }

    private Task SyncProgramRulesAsync() => runtime.Rules.SyncAsync();
    private string? rulesShownName;
    private void RulesChanged()
    {
        if (closed) return;
        if (rulesShownName != ruleSetName)
        {
            rulesShownName = ruleSetName;
            RefreshRules(); RefreshRuleSetButton();
            if (profile is not null) SelectProfile(profile);
        }
        if (ruleApplyProblem is not null) DetailText.Text = ruleApplyProblem;
        RefreshRuleApplyStatus();
        _ = RefreshConnectionsAsync();
    }
}

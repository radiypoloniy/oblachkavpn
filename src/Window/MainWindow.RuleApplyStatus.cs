using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private string? ruleApplyProblem;
    private SentRules? pendingRulesBefore;

    private void WireRuleApplyStatus()
    {
        Routes.RetryRules.Click += (_, _) => ProgramRulesChanged();
        Routes.ReconnectRules.Click += async (_, _) => await SelectiveChangedAsync();
    }

    private void RefreshRuleApplyStatus()
    {
        var pending = SystemTunnelActive && !tunnelDirectAllowed &&
            (siteRules.Any(r => r.Action == SiteAction.Direct) || programRules.Any(r => r.Action == SiteAction.Direct));
        var text = pending ? "Правило «напрямую» сохранено, но это подключение запущено без прямого выхода. Переподключите VPN, чтобы применить исключение."
            : SystemTunnelActive ? ruleApplyProblem : null;
        Routes.ApplyStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        Routes.ApplyStatusText.Text = text ?? "";
        Routes.RetryRules.Visibility = !pending && ruleApplyProblem is not null ? Visibility.Visible : Visibility.Collapsed;
        Routes.ReconnectRules.IsEnabled = operation is null && !systemStopping;
    }
}

using System.Windows;

namespace RoutePilot;

public partial class MainWindow
{
    private readonly List<SiteRule> siteRules = new();
    private void WireRules()
    {
        Routes.AddRuleButton.Click += (_, _) =>
        {
            try
            {
                if (siteRules.Count >= 100) throw new UserError("Достигнут предел 100 правил.");
                var rule = RoutingPlan.Create(Routes.RuleDomain.Text, (SiteAction)Routes.RuleAction.SelectedIndex);
                if (siteRules.Any(r => r.Domain == rule.Domain)) throw new UserError("Этот домен уже добавлен. Удали старое правило, чтобы изменить действие.");
                siteRules.Add(rule); Routes.RuleDomain.Clear(); RefreshRules(); QueueStateSave();
            }
            catch (UserError ex) { DetailText.Text = ex.Message; }
        };
        Routes.RemoveRuleButton.Click += (_, _) =>
        { if (Routes.RuleList.SelectedItem is SiteRule rule) { siteRules.Remove(rule); RefreshRules(); QueueStateSave(); } };
        Routes.ValidateRulesButton.Click += async (_, _) =>
        {
            if (operation is not null || profile is null) return;
            operation = new CancellationTokenSource(); UpdateControls();
            try
            {
                var adapter = new CoreAdapter(profile.Core, Path.Combine(AppContext.BaseDirectory, "tools", profile.Core == CoreKind.Xray ? "xray.exe" : "sing-box.exe"));
                var config = RoutingPlan.Compile(adapter, profile, 10808, siteRules);
                await CoreConfigurationCheck.ValidateAsync(adapter, config, operation.Token);
                if (!closed) DetailText.Text = "Ядро приняло черновик правил. Текущее подключение не изменялось.";
            }
            catch (OperationCanceledException) { }
            catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
            finally { operation.Dispose(); operation = null; if (!closed) UpdateControls(); }
        };
    }
    private void RefreshRules() { Routes.RuleList.ItemsSource = siteRules.ToArray(); }
}

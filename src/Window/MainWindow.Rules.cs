using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private readonly List<SiteRule> siteRules = new();
    private void WireRules()
    {
        WireProgramRules();
        WireRouting();
        // Новое правило идёт «через VPN»; действие меняется нажатием на метку в списке.
        Routes.AddRuleButton.Click += (_, _) => AddSiteRule(Routes.RuleDomain.Text, SiteAction.Tunnel);
        Routes.RuleDomain.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddSiteRule(Routes.RuleDomain.Text, SiteAction.Tunnel); };
        Advanced.ValidateRulesButton.Click += async (_, _) =>
        {
            if (operation is not null || profile is null) return;
            operation = new CancellationTokenSource(); UpdateControls();
            try
            {
                var adapter = new CoreAdapter(profile.Core, Path.Combine(AppContext.BaseDirectory, "tools", profile.Core == CoreKind.Xray ? "xray.exe" : "sing-box.exe"));
                var config = RoutingPlan.Compile(adapter, profile, 10808, siteRules);
                await CoreConfigurationCheck.ValidateAsync(adapter, config, operation.Token);
                await ValidateProgramDraftAsync(operation.Token);
                if (!closed) DetailText.Text = "Ядро приняло правила. Они вступят в силу со следующего подключения.";
            }
            catch (OperationCanceledException) { }
            catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
            finally { operation.Dispose(); operation = null; if (!closed) UpdateControls(); }
        };
    }
    private void AddSiteRule(string domain, SiteAction action)
    {
        try
        {
            if (siteRules.Count >= 100) throw new UserError("Достигнут предел 100 правил.");
            var rule = RoutingPlan.Create(domain, action);
            if (siteRules.Any(r => r.Domain == rule.Domain)) throw new UserError("Этот домен уже добавлен: нажмите на его метку, чтобы сменить действие.");
            siteRules.Add(rule); Routes.RuleDomain.Clear(); RefreshRules(); QueueStateSave();
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    private void RefreshRules()
    {
        Routes.RuleList.ItemsSource = siteRules.Select(r => new RuleRow { Rule = r, Text = r.Domain, Action = r.Action }).ToArray();
        Routes.ProgramList.ItemsSource = programRules.Select(ProgramRow).ToArray();
        if (viewsReady) { UpdateWhere(); RefreshSettingsSummary(); }
    }
}

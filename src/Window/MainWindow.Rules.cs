using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private List<SiteRule> siteRules => runtime.TunnelPolicy.Sites;
    private void WireRules()
    {
        WireRuleApplyStatus();
        WireFree();
        WireAdvanced(); RestoreAdvanced(advanced); WireRulesFile(); WireRuleSets();
        WireConnections();
        WireSelective();
        WireRouting();
        WireQuickRouting();
        Diagnostics.ValidateRulesButton.Click += async (_, _) =>
        {
            if (operation is not null || profile is null) return;
            operation = new CancellationTokenSource(); UpdateControls();
            try
            {
                var adapter = new CoreAdapter(profile.Core, Path.Combine(AppContext.BaseDirectory, "tools", CoreAdapter.FileFor(profile)));
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
    private void RulesEdited(bool editor = true)
    {
        RefreshRules();
        if (editor && ruleSetDraft is not null) return;
        QueueStateSave();
        ProgramRulesChanged();
    }

    private void AddSiteRule(string domain, SiteAction action)
    {
        try
        {
            if (EditedSites.Count >= 100) throw new UserError("Достигнут предел 100 правил.");
            var rule = RoutingPlan.Create(SiteRuleInput.Target(domain), action);
            if (EditedSites.Any(r => r.Domain == rule.Domain)) throw new UserError("Этот домен уже добавлен: нажмите на его метку, чтобы сменить действие.");
            if (!ConfirmPinnedConflicts([rule], [])) return;
            EditedSites.Add(rule); Routes.RuleDomain.Clear(); RulesEdited();
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    private void RefreshRules()
    {
        var paths = KnownProgramPaths();
        var conflicts = ProgramRuleConflicts.Find(programRules, paths);
        var editorConflicts = ruleSetDraft is null ? conflicts : ProgramRuleConflicts.Find(EditedPrograms, paths);
        Routes.SetRules(EditedSites.Select(r => new RuleRow { Rule = r, Text = r.Domain, Action = r.Action })
            .Concat(EditedPrograms.Select(r => ProgramRow(r, editorConflicts))).ToArray());
        // Тот же спор — плашкой в «Программах и сайтах»: там его видно раньше, чем в правилах.
        Live.ConflictBanner.Visibility = conflicts.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (conflicts.Count > 0)
            Live.ConflictText.Text = ProgramRuleConflicts.Note(conflicts[0].Name, conflicts) + (conflicts.Count > 1 ? " И ещё споров: " + (conflicts.Count - 1) + "." : "");
        if (viewsReady) RefreshSettingsSummary();
    }
}

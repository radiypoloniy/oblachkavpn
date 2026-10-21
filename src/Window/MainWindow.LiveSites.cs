using Oblachka.Views;

namespace Oblachka;

// Правила сайтов из «Сейчас в сети»: «YouTube только в Chrome напрямую» (правило программы с сайтом,
// ProgramRule.Domain) или «YouTube во всех программах напрямую» (обычное правило сайта). Под
// «Весь компьютер» оба действуют сразу: ядро туннеля перечитывает файлы правил (ProgramRuleSets,
// SiteRuleSets), а окно закрывает только соединения с этим сайтом.
public partial class MainWindow
{
    private void WireLiveSites() => Live.SiteActionChosen += ApplySiteRule;

    // Подписи и выбранные кнопки строки сайта — по правилам, что есть сейчас.
    private void ShowSiteRules(ConnectionRow row, SiteRow site)
    {
        site.OwnerPath = row.Path;
        site.AppLabel = "Только в " + row.Name;
        site.AppConfigurable = row.Configurable;
        if (site.Domain is null)
        {
            try { site.Domain = RoutingPlan.Create(site.Name, SiteAction.Tunnel).Domain; }
            catch (UserError) { site.Domain = null; }
        }
        if (site.Domain is not { } domain) return;
        var keys = ScopeKeys(row.Path);
        site.AppRule = programRules.FirstOrDefault(r => r.Domain == domain && keys.Contains(r.TargetKey))?.Action;
        site.AllRule = siteRules.FirstOrDefault(r => r.Domain == domain)?.Action;
        // Правило программы сильнее правила сайта для всех программ — иначе человек не поймёт, почему не сработало.
        site.Note = site.AllRule is not null && site.AppRule is null && row.Rule is not null
            ? "У " + row.Name + " своё правило — оно сильнее правила для всех программ. Выберите путь «Только в " + row.Name + "»." : "";
    }

    private HashSet<string> ScopeKeys(string path) =>
        (TryScope(path)?.Rules.Select(r => r.TargetKey) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void ApplySiteRule(SiteRow site, bool app, SiteAction? action)
    {
        if (site.Domain is not { } domain) return;
        if (app)
        {
            if (!site.AppConfigurable || TryScope(site.OwnerPath) is not { } scope) return;
            var keys = scope.Rules.Select(r => r.TargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (action is null)
            {
                programRules.RemoveAll(r => r.Domain == domain && keys.Contains(r.TargetKey));
                RefreshRules(); QueueStateSave(); ProgramRulesChanged();
            }
            else if (!AddProgramRules(scope.Rules.Select(r => r with { Action = action.Value, Domain = domain }).ToArray())) return;
        }
        else
        {
            var index = siteRules.FindIndex(r => r.Domain == domain);
            if (action is null) { if (index >= 0) siteRules.RemoveAt(index); }
            else if (index >= 0) siteRules[index] = siteRules[index] with { Action = action.Value };
            else if (siteRules.Count >= 100) { DetailText.Text = "Достигнут предел 100 правил сайтов: удалите лишние в «Правилах»."; return; }
            else siteRules.Add(RoutingPlan.Create(domain, action.Value));
            RefreshRules(); QueueStateSave(); ProgramRulesChanged();
        }
        if (action is { } chosen && SystemTunnelActive) site.SwitchTo(chosen);
        StartLiveBurst();
        _ = RefreshConnectionsAsync();
        if (!SystemTunnelActive) DetailText.Text = app ? "Правило сохранено. Заработает в режиме «Весь компьютер»." : "Правило сайта сохранено.";
    }
}

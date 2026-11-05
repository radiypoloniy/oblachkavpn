using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Oblachka;

// Наборы правил («Дом», «Работа», «Игры»; «Для продвинутых», П2). Текущие правила — в siteRules и
// programRules, как раньше; остальные наборы лежат в состоянии. Переключение запоминает текущие в
// своём наборе и применяет выбранный — на ходу, как любая правка правил.
// Servers — через какие страны (машины, ServerMachines.Hash) можно ходить в этом наборе; null — через любые.
public sealed record RuleSet(string Name, SiteRule[] Sites, ProgramRule[] Programs, string[]? Servers = null);

public partial class MainWindow
{
    public const string MainRuleSet = ClientWorkspace.MainRuleSet;
    public const int MaxRuleSets = 8;
    private List<RuleSet> ruleSets => runtime.Workspace.RuleSets;
    private string ruleSetName { get => runtime.Workspace.RuleSetName; set => runtime.Workspace.RuleSetName = value; }
    private sealed class RuleSetDraft
    {
        public List<SiteRule> Sites { get; } = new();
        public List<ProgramRule> Programs { get; } = new();
    }
    private RuleSetDraft? ruleSetDraft;
    private List<SiteRule> EditedSites => ruleSetDraft?.Sites ?? siteRules;
    private List<ProgramRule> EditedPrograms => ruleSetDraft?.Programs ?? programRules;

    private void WireRuleSets()
    {
        Routes.RuleSetButton.Click += (_, _) => { FillRuleSetMenu(); Routes.RuleSetPopup.IsOpen = true; };
        Routes.NewRuleSet.Click += (_, _) => { Routes.RuleSetPopup.IsOpen = false; StartDraft(); Routes.RuleDomain.Focus(); };
        Routes.DeleteRuleSet.Click += (_, _) => DeleteRuleSet();
        Routes.SaveRuleSet.Click += (_, _) => SaveDraft();
        Routes.CancelRuleSet.Click += (_, _) => CancelDraft();
        WireRuleSetServers();
        RefreshRuleSetButton();
    }

    private void RestoreRuleSets(RuleSet[]? saved, string? current, string[]? currentServers)
    {
        ruleSets.Clear(); ruleSetDraft = null;
        foreach (var set in saved ?? [])
        {
            // Набор, не прошедший проверку, не загружается: правила оттуда ушли бы в ядро.
            try { RoutingPlan.Validate(set.Sites); ProgramRoutingPlan.Validate(set.Programs); }
            catch (UserError) { continue; }
            if (ValidName(set.Name) && ruleSets.All(s => s.Name != set.Name) && ruleSets.Count < MaxRuleSets) ruleSets.Add(set with { Servers = CleanServers(set.Servers)?.ToArray() });
        }
        ruleSetName = current is { } name && ValidName(name) ? name : MainRuleSet;
        ruleSetServers = CleanServers(currentServers);
        RefreshRuleSetButton();
    }

    internal static bool ValidName(string name) => name.Trim().Length is >= 1 and <= 24 && name == name.Trim();

    // Снимок действующих и сохранённых наборов, отдельно от редактора.
    private RuleSet[] SavedRuleSets() => runtime.Workspace.SavedRuleSets(runtime);
    private RuleSet PersistedSet => runtime.Workspace.PersistedSet(runtime);

    private void SwitchRuleSet(string name)
    {
        Routes.RuleSetPopup.IsOpen = false;
        CloseRouteEditor();
        ClearQuickSuggestion();
        ClearServiceSuggestion();
        if (ruleSetDraft is not null) CancelDraft();
        if (name == ruleSetName) return;
        var all = SavedRuleSets();
        var next = all.FirstOrDefault(s => s.Name == name) ?? new RuleSet(name, [], []);
        if (!CheckPinnedServers(CleanServers(next.Servers)) || !ConfirmPinnedConflicts(next.Sites, next.Programs)) return;
        ruleSets.Clear(); ruleSets.AddRange(all);
        ruleSetName = name;
        Load(next);
        DetailText.Text = "Набор правил «" + name + "»: сайтов " + siteRules.Count + ", программ " + programRules.Count + ".";
    }

    private void Load(RuleSet set)
    {
        siteRules.Clear(); siteRules.AddRange(set.Sites);
        programRules.Clear(); programRules.AddRange(set.Programs);
        ruleSetServers = CleanServers(set.Servers);
        RefreshRules(); QueueStateSave(); ProgramRulesChanged(); RefreshRuleSetButton();
        EnforceRuleSetServers();
    }

    // «Новый набор»: нынешние правила остаются в своём наборе, список — с чистого листа.
    private void StartDraft()
    {
        if (ruleSetDraft is not null) return;
        if (SavedRuleSets().Length >= MaxRuleSets) { DetailText.Text = "Наборов не больше " + MaxRuleSets + ". Удалите лишний."; return; }
        CloseRouteEditor();
        ClearQuickSuggestion();
        ClearServiceSuggestion();
        ruleSetDraft = new();
        RefreshRules(); RefreshRuleSetButton();
        DetailText.Text = "Новый набор: добавьте сайты и программы. Действующий набор — «" + ruleSetName + "».";
    }

    private void SaveDraft()
    {
        if (ruleSetDraft is null) return;
        var name = AppDialog.Ask(this, "Название набора", "Например «Работа» или «Игры». Включать набор можно здесь же или сам по сети — в «Приложении».", "Название набора", "Сохранить")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        CommitDraft(name);
    }

    private bool CommitDraft(string name)
    {
        if (ruleSetDraft is not { } draft) return false;
        var all = SavedRuleSets();
        if (!ValidName(name) || all.Any(s => s.Name == name))
        {
            DetailText.Text = "Нужно новое название до 24 символов.";
            return false;
        }
        if (all.Length >= MaxRuleSets)
        {
            DetailText.Text = "Наборов не больше " + MaxRuleSets + ". Удалите лишний.";
            return false;
        }

        var next = new RuleSet(name, draft.Sites.ToArray(), draft.Programs.ToArray());
        if (!ConfirmPinnedConflicts(next.Sites, next.Programs)) return false;
        CloseRouteEditor();
        ruleSets.Clear(); ruleSets.AddRange(all);
        ruleSetDraft = null; ruleSetName = name;
        Load(next);
        DetailText.Text = "Набор «" + name + "» сохранён и выбран.";
        return true;
    }

    private void CancelDraft()
    {
        if (ruleSetDraft is null) return;
        CloseRouteEditor();
        ClearQuickSuggestion();
        ClearServiceSuggestion();
        ruleSetDraft = null;
        RefreshRules(); RefreshRuleSetButton();
        DetailText.Text = "Новый набор отменён. Действующий набор — «" + ruleSetName + "».";
    }

    private void DeleteRuleSet()
    {
        Routes.RuleSetPopup.IsOpen = false;
        if (ruleSetName == MainRuleSet || ruleSetDraft is not null) return;
        var next = ruleSets.FirstOrDefault(s => s.Name == MainRuleSet) ?? new RuleSet(MainRuleSet, [], []);
        if (!CheckPinnedServers(CleanServers(next.Servers)) || !ConfirmPinnedConflicts(next.Sites, next.Programs)) return;
        if (!AppDialog.Confirm(this, "Удалить набор «" + ruleSetName + "»?", "Его правила уберутся, включится набор «" + MainRuleSet + "».", "Удалить", danger: true)) return;
        var removed = ruleSetName;
        ruleSets.RemoveAll(s => s.Name == removed);
        ruleSetName = MainRuleSet;
        Load(next);
    }

    private void RefreshRuleSetButton()
    {
        var draft = ruleSetDraft is not null;
        Routes.ShowPinnedRules(!draft);
        Routes.RuleSetBar.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;
        Routes.RuleSetDraft.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        Routes.RuleSetText.Text = ruleSetName;
        Routes.DeleteRuleSet.Visibility = ruleSetName == MainRuleSet ? Visibility.Collapsed : Visibility.Visible;
        RefreshRuleSetServersText();
    }

    private void FillRuleSetMenu()
    {
        var items = Routes.RuleSetItems.Children;
        items.Clear();
        foreach (var set in SavedRuleSets())
        {
            var name = set.Name;
            var check = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IcoCheck"), Style = (Style)FindResource("Icon"), Width = 14, Height = 14, StrokeThickness = 2.4,
                Visibility = name == ruleSetName ? Visibility.Visible : Visibility.Hidden, Margin = new Thickness(8, 0, 0, 0) };
            check.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Ac");
            DockPanel.SetDock(check, Dock.Right);
            var label = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            var count = new TextBlock { Text = "Правил: " + (set.Sites.Length + set.Programs.Length), Style = (Style)FindResource("Small"), Margin = new Thickness(0, 2, 0, 0) };
            var row = new DockPanel { Children = { check, new StackPanel { Children = { label, count } } } };
            var button = new Button { Content = row, Style = (Style)Routes.FindResource("RuleMenuRow") };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            if (name == ruleSetName) button.SetResourceReference(Control.BackgroundProperty, "Lilac");
            button.Click += (_, _) => SwitchRuleSet(name);
            items.Add(button);
        }
    }
}

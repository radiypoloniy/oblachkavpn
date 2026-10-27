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
    public const string MainRuleSet = "Основной";
    public const int MaxRuleSets = 8;
    private readonly List<RuleSet> ruleSets = new();
    private string ruleSetName = MainRuleSet;
    // Черновик нового набора (0.21): правила с чистого листа, имя — при сохранении. draftBase — набор,
    // что был включён до черновика: в состоянии остаётся он, «Отмена» возвращает его.
    private RuleSet? draftBase;

    private void WireRuleSets()
    {
        Routes.RuleSetButton.Click += (_, _) => { FillRuleSetMenu(); Routes.RuleSetPopup.IsOpen = true; };
        Routes.NewRuleSet.Click += (_, _) => StartDraft();
        Routes.DeleteRuleSet.Click += (_, _) => DeleteRuleSet();
        Routes.SaveRuleSet.Click += (_, _) => SaveDraft();
        Routes.CancelRuleSet.Click += (_, _) => CancelDraft();
        WireRuleSetServers();
        RefreshRuleSetButton();
    }

    private void RestoreRuleSets(RuleSet[]? saved, string? current, string[]? currentServers)
    {
        ruleSets.Clear(); draftBase = null;
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

    // Все наборы для сохранения: текущий — из списка правил. В черновике текущих правил в наборах нет.
    private RuleSet[] SavedRuleSets() => draftBase is not null ? ruleSets.ToArray() :
        ruleSets.Where(s => s.Name != ruleSetName).Append(new RuleSet(ruleSetName, siteRules.ToArray(), programRules.ToArray(), ruleSetServers?.ToArray()))
            .OrderBy(s => s.Name == MainRuleSet ? 0 : 1).ToArray();

    // Что пишется в состояние как «текущий набор»: в черновике — набор до него, черновик не сохраняется.
    private RuleSet PersistedSet => draftBase ?? new RuleSet(ruleSetName, siteRules.ToArray(), programRules.ToArray(), ruleSetServers?.ToArray());

    private void SwitchRuleSet(string name)
    {
        Routes.RuleSetPopup.IsOpen = false;
        if (draftBase is not null) CancelDraft();
        if (name == ruleSetName) return;
        var all = SavedRuleSets();
        var next = all.FirstOrDefault(s => s.Name == name) ?? new RuleSet(name, [], []);
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
        if (draftBase is not null) return;
        if (SavedRuleSets().Length >= MaxRuleSets) { DetailText.Text = "Наборов не больше " + MaxRuleSets + ". Удалите лишний."; return; }
        var all = SavedRuleSets();
        ruleSets.Clear(); ruleSets.AddRange(all);
        draftBase = all.First(s => s.Name == ruleSetName);
        siteRules.Clear(); programRules.Clear(); ruleSetServers = null;
        RefreshRules(); ProgramRulesChanged(); RefreshRuleSetButton();
        DetailText.Text = "Новый набор: добавьте сайты и программы и нажмите «Сохранить». «Отмена» вернёт «" + draftBase.Name + "».";
    }

    private void SaveDraft()
    {
        if (draftBase is null) return;
        var name = AppDialog.Ask(this, "Название набора", "Например «Работа» или «Игры». Включать набор можно здесь же или сам по сети — в «Приложении».", "Название набора", "Сохранить")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!ValidName(name) || ruleSets.Any(s => s.Name == name)) { DetailText.Text = "Нужно новое название до 24 символов."; return; }
        draftBase = null; ruleSetName = name;
        QueueStateSave(); RefreshRuleSetButton();
        DetailText.Text = "Набор «" + name + "» сохранён: сайтов " + siteRules.Count + ", программ " + programRules.Count + ".";
    }

    private void CancelDraft()
    {
        if (draftBase is not { } back) return;
        draftBase = null; ruleSetName = back.Name;
        Load(back);
        DetailText.Text = "Новый набор отменён, включён «" + back.Name + "».";
    }

    private void DeleteRuleSet()
    {
        Routes.RuleSetPopup.IsOpen = false;
        if (ruleSetName == MainRuleSet || draftBase is not null) return;
        if (!AppDialog.Confirm(this, "Удалить набор «" + ruleSetName + "»?", "Его правила уберутся, включится набор «" + MainRuleSet + "».", "Удалить", danger: true)) return;
        var removed = ruleSetName;
        ruleSets.RemoveAll(s => s.Name == removed);
        ruleSetName = MainRuleSet;
        Load(ruleSets.FirstOrDefault(s => s.Name == MainRuleSet) ?? new RuleSet(MainRuleSet, [], []));
    }

    private void RefreshRuleSetButton()
    {
        var draft = draftBase is not null;
        Routes.RuleSetBar.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;
        Routes.RuleSetDraft.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        Routes.RuleSetText.Text = "Набор: " + ruleSetName;
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
                Visibility = name == ruleSetName ? Visibility.Visible : Visibility.Hidden };
            DockPanel.SetDock(check, Dock.Right);
            var row = new DockPanel { Children = { check, new TextBlock { Text = name + "  ·  правил " + (set.Sites.Length + set.Programs.Length), VerticalAlignment = VerticalAlignment.Center } } };
            var button = new Button { Content = row, Style = (Style)FindResource("MenuRow") };
            button.Click += (_, _) => SwitchRuleSet(name);
            items.Add(button);
        }
    }
}

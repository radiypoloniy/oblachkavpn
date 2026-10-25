using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Oblachka;

// Наборы правил («Дом», «Работа», «Игры»; «Для продвинутых», П2). Текущие правила — в siteRules и
// programRules, как раньше; остальные наборы лежат в состоянии. Переключение запоминает текущие в
// своём наборе и применяет выбранный — на ходу, как любая правка правил.
public sealed record RuleSet(string Name, SiteRule[] Sites, ProgramRule[] Programs);

public partial class MainWindow
{
    public const string MainRuleSet = "Основной";
    public const int MaxRuleSets = 8;
    private readonly List<RuleSet> ruleSets = new();
    private string ruleSetName = MainRuleSet;

    private void WireRuleSets()
    {
        Routes.RuleSetButton.Click += (_, _) => { FillRuleSetMenu(); Routes.RuleSetPopup.IsOpen = true; };
        RefreshRuleSetButton();
    }

    private void RestoreRuleSets(RuleSet[]? saved, string? current)
    {
        ruleSets.Clear();
        foreach (var set in saved ?? [])
        {
            // Набор, не прошедший проверку, не загружается: правила оттуда ушли бы в ядро.
            try { RoutingPlan.Validate(set.Sites); ProgramRoutingPlan.Validate(set.Programs); }
            catch (UserError) { continue; }
            if (ValidName(set.Name) && ruleSets.All(s => s.Name != set.Name) && ruleSets.Count < MaxRuleSets) ruleSets.Add(set);
        }
        ruleSetName = current is { } name && ValidName(name) ? name : MainRuleSet;
        RefreshRuleSetButton();
    }

    internal static bool ValidName(string name) => name.Trim().Length is >= 1 and <= 24 && name == name.Trim();

    // Все наборы для сохранения: текущий — из списка правил.
    private RuleSet[] SavedRuleSets() =>
        ruleSets.Where(s => s.Name != ruleSetName).Append(new RuleSet(ruleSetName, siteRules.ToArray(), programRules.ToArray()))
            .OrderBy(s => s.Name == MainRuleSet ? 0 : 1).ToArray();

    private void SwitchRuleSet(string name)
    {
        Routes.RuleSetPopup.IsOpen = false;
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
        RefreshRules(); QueueStateSave(); ProgramRulesChanged(); RefreshRuleSetButton();
    }

    private void SaveRuleSetAs()
    {
        Routes.RuleSetPopup.IsOpen = false;
        if (SavedRuleSets().Length >= MaxRuleSets) { DetailText.Text = "Наборов не больше " + MaxRuleSets + ". Удалите лишний."; return; }
        var name = AppDialog.Ask(this, "Новый набор правил", "Сохранить нынешние правила под новым именем — например «Работа» или «Игры».", "Название набора", "Сохранить")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!ValidName(name) || SavedRuleSets().Any(s => s.Name == name)) { DetailText.Text = "Нужно новое название до 24 символов."; return; }
        var current = SavedRuleSets();
        ruleSets.Clear(); ruleSets.AddRange(current);
        ruleSetName = name;
        QueueStateSave(); RefreshRuleSetButton();
        DetailText.Text = "Набор «" + name + "» сохранён — переключаться можно кнопкой «Набор» в «Правилах».";
    }

    private void DeleteRuleSet()
    {
        Routes.RuleSetPopup.IsOpen = false;
        if (ruleSetName == MainRuleSet) return;
        if (!AppDialog.Confirm(this, "Удалить набор «" + ruleSetName + "»?", "Его правила уберутся, включится набор «" + MainRuleSet + "».", "Удалить", danger: true)) return;
        var removed = ruleSetName;
        ruleSets.RemoveAll(s => s.Name == removed);
        ruleSetName = MainRuleSet;
        Load(ruleSets.FirstOrDefault(s => s.Name == MainRuleSet) ?? new RuleSet(MainRuleSet, [], []));
    }

    private void RefreshRuleSetButton() => Routes.RuleSetText.Text = "Набор: " + ruleSetName;

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
        var line = new Border { Height = 1.5, Margin = new Thickness(-14, 4, -14, 4) };
        line.SetResourceReference(Border.BackgroundProperty, "Divider");
        items.Add(line);
        var save = new Button { Content = "Сохранить как новый набор…", Style = (Style)FindResource("MenuRow") };
        save.Click += (_, _) => SaveRuleSetAs();
        items.Add(save);
        if (ruleSetName == MainRuleSet) return;
        var delete = new Button { Content = "Удалить этот набор", Style = (Style)FindResource("MenuRow") };
        delete.SetResourceReference(ForegroundProperty, "RF");
        delete.Click += (_, _) => DeleteRuleSet();
        items.Add(delete);
    }
}

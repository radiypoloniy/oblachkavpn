using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Серверы набора правил (0.21): в «Работе» — только Германия и Нидерланды, в «Играх» — только ближний
// сервер. Отмечаются страны целиком (машины, ServerMachines) — со всеми протоколами. Автовыбор и
// подбор после обрыва берут только их (InAutoPool); сервер не из набора руками — с предупреждением.
public partial class MainWindow
{
    // null — через любые серверы.
    private HashSet<string>? ruleSetServers { get => runtime.Profiles.RuleSetServers; set => runtime.Profiles.RuleSetServers = value; }

    private void WireRuleSetServers()
    {
        Routes.RuleSetServers.Click += (_, _) => { FillRuleSetServers(); Routes.RuleSetServersPopup.IsOpen = true; };
    }

    private static HashSet<string>? CleanServers(string[]? saved) =>
        saved is null ? null : saved.Where(s => s is { Length: 16 } && s.All(char.IsAsciiHexDigit)).Take(Subscriptions.MaxProfiles).ToHashSet();

    private bool InRuleSet(Profile item) => ruleSetServers is null || !item.IsFree && ruleSetServers.Contains(ServerMachines.Hash(item));

    // Подпись у строки сервера, через который в этом наборе ходить нельзя.
    private string? RuleSetNote(Profile item) => InRuleSet(item) ? null : "не в наборе «" + ruleSetName + "»";

    private IReadOnlyList<IReadOnlyList<Profile>> RuleSetMachines() => ServerMachines.Group(importedProfiles.Where(p => !p.IsFree));

    private void RefreshRuleSetServersText()
    {
        if (ruleSetServers is null) { Routes.RuleSetServersText.Text = "Серверы: любые"; return; }
        var names = RuleSetMachines().Where(m => ruleSetServers.Contains(ServerMachines.Hash(m[0]))).Select(ServerMachines.Title).ToArray();
        Routes.RuleSetServersText.Text = "Серверы: " + (names.Length is > 0 and <= 2 ? string.Join(", ", names) : "только " + names.Length);
    }

    // Строки меню в облике приложения (как список наборов), не галочки. Пока «Любые» — нажатие на страну
    // оставляет только её: один сервер — одно нажатие. Дальше нажатие добавляет или убирает страну;
    // убрали последнюю — снова «Любые».
    private void FillRuleSetServers()
    {
        var items = Routes.RuleSetServerItems.Children;
        items.Clear();
        items.Add(ServerMenuRow("Любые серверы", null, ruleSetServers is null, () => SetRuleSetServers(null)));
        Routes.RuleSetServerNote.Text = ruleSetServers is null ? "Выберите сервер, чтобы ограничить набор." : "Нажмите на сервер, чтобы добавить или убрать его.";
        foreach (var machine in RuleSetMachines())
        {
            var key = ServerMachines.Hash(machine[0]);
            items.Add(ServerMenuRow(ServerMachines.Title(machine), machine[0], ruleSetServers?.Contains(key) == true, () =>
            {
                var next = new HashSet<string>(ruleSetServers ?? []);
                if (!next.Remove(key)) next.Add(key);
                SetRuleSetServers(next.Count == 0 ? null : next);
            }));
        }
    }

    private Button ServerMenuRow(string text, Profile? sample, bool on, Action act)
    {
        var check = new System.Windows.Shapes.Path { Data = (System.Windows.Media.Geometry)FindResource("IcoCheck"), Style = (Style)FindResource("Icon"), Width = 14, Height = 14, StrokeThickness = 2.4,
            Visibility = on ? Visibility.Visible : Visibility.Hidden, Margin = new Thickness(8, 0, 0, 0) };
        check.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Ac");
        DockPanel.SetDock(check, Dock.Right);
        var row = new DockPanel { Children = { check } };
        if (sample is not null)
        {
            // Без флага — пустое место того же размера: названия стоят ровной колонкой.
            var ring = new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), StrokeThickness = 1 };
            if (FlagImages.Local(sample.Country.Code) is { } flag)
            {
                ring.Fill = new System.Windows.Media.ImageBrush(flag) { Stretch = System.Windows.Media.Stretch.UniformToFill };
                ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FlagRing");
            }
            row.Children.Add(ring);
        }
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal });
        var button = new Button { Content = row, Style = (Style)Routes.FindResource("RuleMenuRow") };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        // Выбранное — сиренью, как выбранная строка «Серверов».
        if (on) button.SetResourceReference(BackgroundProperty, "Lilac");
        button.Click += (_, _) => act();
        return button;
    }

    private void SetRuleSetServers(HashSet<string>? next)
    {
        if (!CheckPinnedServers(next)) return;
        ruleSetServers = next;
        QueueStateSave(); RefreshRuleSetServersText(); FillRuleSetServers(); RenderSheet();
        EnforceRuleSetServers();
    }

    private bool CheckPinnedServers(IReadOnlySet<string>? next)
    {
        var excluded = runtime.ServiceRoutes.ExcludedBy(next);
        if (excluded.Length == 0) return true;
        DetailText.Text = "Набор исключает серверы для привязок " + string.Join(", ", excluded) + ". Сначала измените привязки или список серверов.";
        return false;
    }

    // Включён набор, а выбран сервер не из него: выбор переходит на лучший разрешённый (на ходу, если VPN включён).
    private void EnforceRuleSetServers()
    {
        if (profile is null || InRuleSet(profile)) { RenderSheet(); return; }
        var allowed = importedProfiles.Where(InRuleSet).ToArray();
        if (allowed.Length == 0) { DetailText.Text = "В наборе «" + ruleSetName + "» нет серверов из подписок: подходит любой."; return; }
        var best = ServerMeasurements.Best(allowed, EffectiveMeasurements(), DateTimeOffset.UtcNow, null) is int i ? allowed[i] : allowed[0];
        if (!autoSelect) { machineMode = MachineOf(best).Count > 1; if (machineMode) best = MachineBest(MachineOf(best)); }
        DetailText.Text = profile.DisplayName + " не в наборе «" + ruleSetName + "» — выбран " + best.DisplayName + ".";
        SelectProfile(best);
        RenderSheet();
    }

    // Руками выбран сервер не из набора: предупредить и предложить снять ограничение.
    private bool AllowOutsideRuleSet(Profile item)
    {
        if (InRuleSet(item)) return true;
        if (!AppDialog.Confirm(this, item.DisplayName + " не в наборе «" + ruleSetName + "»",
                "В этом наборе можно ходить только через отмеченные серверы. Выключить ограничение и подключиться?", "Выключить ограничение")) return false;
        SetRuleSetServers(null);
        return true;
    }
}

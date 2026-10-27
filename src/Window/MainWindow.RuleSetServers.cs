using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Серверы набора правил (0.21): в «Работе» — только Германия и Нидерланды, в «Играх» — только ближний
// сервер. Отмечаются страны целиком (машины, ServerMachines) — со всеми протоколами. Автовыбор и
// подбор после обрыва берут только их (InAutoPool); сервер не из набора руками — с предупреждением.
public partial class MainWindow
{
    // null — через любые серверы.
    private HashSet<string>? ruleSetServers;

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

    private void FillRuleSetServers()
    {
        var items = Routes.RuleSetServerItems.Children;
        items.Clear();
        var any = new CheckBox { Content = "Любые серверы", IsChecked = ruleSetServers is null, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) };
        any.Click += (_, _) => SetRuleSetServers(any.IsChecked == true ? null : RuleSetMachines().Select(m => ServerMachines.Hash(m[0])).ToHashSet());
        items.Add(any);
        foreach (var machine in RuleSetMachines())
        {
            var key = ServerMachines.Hash(machine[0]);
            var box = new CheckBox { Content = ServerMachines.Title(machine), IsChecked = ruleSetServers is null || ruleSetServers.Contains(key), IsEnabled = ruleSetServers is not null, Margin = new Thickness(0, 3, 0, 3) };
            box.Click += (_, _) =>
            {
                var next = new HashSet<string>(ruleSetServers ?? []);
                if (box.IsChecked == true) next.Add(key); else next.Remove(key);
                // Без единого сервера набор оставил бы VPN без выхода.
                if (next.Count == 0) { box.IsChecked = true; DetailText.Text = "В наборе должен остаться хотя бы один сервер."; return; }
                SetRuleSetServers(next);
            };
            items.Add(box);
        }
    }

    private void SetRuleSetServers(HashSet<string>? next)
    {
        ruleSetServers = next;
        QueueStateSave(); RefreshRuleSetServersText(); FillRuleSetServers(); RenderSheet();
        EnforceRuleSetServers();
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

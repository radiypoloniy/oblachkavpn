using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckClientBlocks(ICollection<string> checks)
    {
        ShowRulesSection();
        Routes.RuleDomain.Text = "https://Example.COM/path?tab=1#section";
        UpdateLayout();
        Descendants<System.Windows.Controls.Button>(Routes.Quick.Results).Single(b => b.Tag is "vpn")
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (siteRules.Count != 1 || siteRules[0].Domain != "example.com" || siteRules[0].Action != SiteAction.Tunnel)
            throw new InvalidOperationException("Rule editor did not normalize the draft");
        // Метка действия меняется по кругу: через VPN → напрямую → блок.
        CycleRule(siteRules[0]); CycleRule(siteRules[0]);
        if (siteRules[0].Action != SiteAction.Block) throw new InvalidOperationException("Rule action badge does not cycle");
        // Путь сайта по правилам — тот, что показывает «Проверить сайт».
        if (SiteRouteByRules("ads.example.com") is not (SiteRoute.Block, var why) || !why.Contains("правило"))
            throw new InvalidOperationException("Site route ignored the matching rule: " + SiteRouteByRules("ads.example.com").Why);
        if (SiteRouteByRules("example.org").Route != SiteRoute.Tunnel) throw new InvalidOperationException("Unmatched site is not shown as tunneled");
        RemoveRule(siteRules[0]);
        if (siteRules.Count != 0) throw new InvalidOperationException("Draft removal failed");
        AddProgramRules([ProgramRoutingPlan.Create("steam.exe", SiteAction.Tunnel)]);
        CycleRule(programRules[0]);
        if (programRules.Count != 1 || programRules[0].Action != SiteAction.Direct) throw new InvalidOperationException("Program draft editor failed");
        RemoveRule(programRules[0]);
        if (programRules.Count != 0) throw new InvalidOperationException("Program draft removal failed");
        CheckUnifiedRouting(checks);
        CheckRuleSetDraft(checks);
        CheckAppPicker(checks);
        // «Сейчас в сети»: процесс с необычным именем файла (claude.exe.old.123) не роняет окно, а
        // показывается без кнопок правил — так упала 0.15.0-rc.3.
        var odd = LiveRow(new LiveApp(@"C:\Apps\Claude\claude.exe.old.1792214641919", 1, 0, 0, ["203.0.113.5:443"]));
        if (odd.Configurable || !odd.Scope.Contains("необычное имя")) throw new InvalidOperationException("Odd process name is configurable in live view");
        checks.Add("Live view shows a process with an odd file name without rule buttons instead of crashing");
        // Доступность измерений зависит от источника узла и проверяется отдельно
        // в CheckPreviewControls и CheckServiceControls.
        if (!Diagnostics.ValidateRulesButton.IsEnabled || backgroundWorkEnabled)
            throw new InvalidOperationException("Client block actions or fixture network isolation incorrect");
        checks.Add("Rule editor normalizes domains, cycles actions, answers «Куда пойдёт сайт?» and removes rules; background requests disabled in fixtures");
    }

    private void CheckUnifiedRouting(ICollection<string> checks)
    {
        var catalog = appCatalog;
        var node = profile ?? throw new InvalidOperationException("Routing fixture has no server");
        var pin = new ServiceRoute("claude", ClientServerCatalog.Id(node)!, ["claude.ai"], []);
        var app = new Views.AppRow(new("Codex", "Codex.exe", @"C:\Apps\Codex\Codex.exe", false), null);
        try
        {
            appCatalog = [app];
            siteRules.Add(RoutingPlan.Create("example.invalid", SiteAction.Direct));
            runtime.TunnelPolicy.ServiceRoutes.Add(pin);
            ShowRulesSection();
            Routes.RuleDomain.Text = "Codex";
            UpdateLayout();
            var edit = Descendants<System.Windows.Controls.Button>(Routes.Quick.Results).Single(b => b.Tag is "edit");
            edit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (Routes.TargetEditor.Visibility != Visibility.Visible || Routes.EditorTitle.Text != app.Name)
                throw new InvalidOperationException("Clicking the search target did not open its inline editor");
            Routes.ServiceAction.SelectedIndex = 3;
            Routes.ServiceSave.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (Routes.TargetEditor.Visibility != Visibility.Collapsed ||
                !Routes.RuleList.Items.OfType<RuleRow>().Any(row => row.Rule is SiteRule) ||
                !Routes.RuleList.Items.OfType<RuleRow>().Any(row => row.Rule is ProgramRule { Action: SiteAction.Block }) ||
                !Routes.RuleList.Items.OfType<ServiceRouteRow>().Any(row => row.Id == pin.Id))
                throw new InvalidOperationException("Site, program, and server rules were not shown together after saving");
            checks.Add("Clicking a search result opens the inline editor, and all three rule types share the saved list");
        }
        finally
        {
            appCatalog = catalog;
            siteRules.RemoveAll(rule => rule.Domain == "example.invalid");
            programRules.RemoveAll(rule => string.Equals(rule.Executable, "Codex.exe", StringComparison.OrdinalIgnoreCase));
            runtime.TunnelPolicy.ServiceRoutes.Remove(pin);
            RefreshRules(); RefreshServiceRoutes();
        }
    }

    private void CheckRuleSetDraft(ICollection<string> checks)
    {
        var active = RoutingPlan.Create("active.invalid", SiteAction.Block);
        var servers = new HashSet<string> { "0123456789abcdef" };
        siteRules.Add(active);
        ruleSetServers = servers;

        StartDraft();
        AddSiteRule("draft.invalid", SiteAction.Direct);
        CycleRule(EditedSites.Single());
        AddProgramRules([ProgramRoutingPlan.Create("steam.exe", SiteAction.Tunnel)], editor: true);
        if (!siteRules.SequenceEqual([active]) || programRules.Count != 0 || ruleSetServers != servers ||
            !PersistedSet.Sites.SequenceEqual([active]) || EditedSites.Single().Action != SiteAction.Block)
            throw new InvalidOperationException("The rule-set editor touched active rules, servers or the persistence snapshot");
        var live = ProgramRoutingPlan.Create("game.exe", SiteAction.Block);
        AddProgramRules([live]);
        CancelDraft();
        if (!siteRules.SequenceEqual([active]) || !programRules.SequenceEqual([live]) || ruleSetServers != servers)
            throw new InvalidOperationException("Cancelling the editor overwrote an active rule change");

        StartDraft();
        AddSiteRule("new.invalid", SiteAction.Tunnel);
        if (CommitDraft(MainRuleSet) || !siteRules.SequenceEqual([active]))
            throw new InvalidOperationException("The editor allowed a duplicate set name");
        if (!CommitDraft("Тест") || ruleSetName != "Тест" || siteRules.Single().Domain != "new.invalid" ||
            !SavedRuleSets().Single(s => s.Name == MainRuleSet).Programs.SequenceEqual([live]))
            throw new InvalidOperationException("Saving did not activate the new set and retain the previous set");
        SwitchRuleSet(MainRuleSet);
        ruleSets.Clear(); siteRules.Clear(); programRules.Clear(); ruleSetServers = null;
        RefreshRules(); RefreshRuleSetButton();
        checks.Add("Rule-set draft edits and cancel leave active rules and servers intact; save activates the new set");
    }

    // Выбор программ: поиск по первым буквам имени, отметка, действие, «Добавить» — в правиле
    // папка программы, в списке правил — имя программы и папка. Каталог подложный: файлы компьютера не читаются.
    private void CheckAppPicker(ICollection<string> checks)
    {
        appCatalog = new[] { new InstalledApp("Telegram Desktop", "Telegram.exe", @"C:\Apps\Telegram Desktop\Telegram.exe", true),
            new InstalledApp("Steam", "steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", false) { Folders = [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary\steamapps\common"] },
            new InstalledApp("Яндекс Музыка", "Яндекс Музыка.exe", @"C:\Apps\Yandex Music\Яндекс Музыка.exe", false) }.Select(a => new Views.AppRow(a, null)).ToArray();
        OpenAppPicker();
        Picker.Load(appCatalog, AddedPrograms());
        Picker.SearchBox.Text = "тел";
        var shown = Picker.AppList.Items.OfType<Views.AppRow>().ToArray();
        if (shown.Length != 1 || shown[0].Name != "Telegram Desktop") throw new InvalidOperationException("Program search by name failed: " + shown.Length);
        foreach (var typed in new[] { "tel", "еуд", "Telegram.e" })
        {
            Picker.SearchBox.Text = typed;
            if (Picker.AppList.Items.OfType<Views.AppRow>().FirstOrDefault()?.Name != "Telegram Desktop") throw new InvalidOperationException("Search failed for " + typed);
        }
        Picker.SearchBox.Text = "yand";
        if (Picker.AppList.Items.OfType<Views.AppRow>().SingleOrDefault()?.Name != "Яндекс Музыка") throw new InvalidOperationException("Latin search for Cyrillic name failed");
        Picker.SearchBox.Text = "муз";
        if (Picker.AppList.Items.OfType<Views.AppRow>().SingleOrDefault()?.Name != "Яндекс Музыка") throw new InvalidOperationException("Search by second word failed");
        Picker.SearchBox.Clear();
        foreach (var row in appCatalog.Where(r => r.App.Executable != "steam.exe")) row.Checked = true;
        Picker.ActionChoice.SelectedIndex = 1;
        Picker.AddButton.IsEnabled = true;
        Picker.AddButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (programRules.Count != 2 || programRules.Any(r => r.Action != SiteAction.Direct) || Picker.Visibility == Visibility.Visible)
            throw new InvalidOperationException("Picked programs were not added as direct rules");
        var listed = Routes.RuleList.Items.OfType<RuleRow>().Where(r => r.Rule is ProgramRule).Select(r => r.Text + "|" + r.Sub).ToArray();
        if (!listed.Contains(@"Telegram Desktop|вся папка · C:\Apps\Telegram Desktop")) throw new InvalidOperationException("Rule list does not show program names: " + string.Join(", ", listed));
        OpenAppPicker();
        if (!appCatalog.Single(r => r.App.Executable == "Telegram.exe").Added) throw new InvalidOperationException("Added program is selectable again");
        CloseAppPicker();
        programRules.Clear(); RefreshRules(); appCatalog = null;
        checks.Add("Program picker finds apps by the first letters of any name word, adds several with one action and lists them by name");
    }
}

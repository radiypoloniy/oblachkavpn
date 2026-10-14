using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckClientBlocks(ICollection<string> checks)
    {
        Routes.RuleDomain.Text = "Example.COM";
        Routes.AddRuleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (siteRules.Count != 1 || siteRules[0].Domain != "example.com" || siteRules[0].Action != SiteAction.Tunnel)
            throw new InvalidOperationException("Rule editor did not normalize the draft");
        // Метка действия меняется по кругу: через VPN → напрямую → блок.
        CycleRule(siteRules[0]); CycleRule(siteRules[0]);
        if (siteRules[0].Action != SiteAction.Block) throw new InvalidOperationException("Rule action badge does not cycle");
        Routes.WhereInput.Text = "ads.example.com";
        if (!Routes.WhereResult.Text.Contains("блок") || !Routes.WhereResult.Text.Contains("example.com"))
            throw new InvalidOperationException("«Куда пойдёт сайт?» ignored the matching rule: " + Routes.WhereResult.Text);
        Routes.WhereInput.Text = "example.org";
        if (!Routes.WhereResult.Text.Contains("через VPN")) throw new InvalidOperationException("Unmatched site is not shown as tunneled");
        Routes.WhereInput.Clear();
        RemoveRule(siteRules[0]);
        if (siteRules.Count != 0) throw new InvalidOperationException("Draft removal failed");
        AddProgramRule("steam.exe", SiteAction.Tunnel);
        CycleRule(programRules[0]);
        if (programRules.Count != 1 || programRules[0].Action != SiteAction.Direct) throw new InvalidOperationException("Program draft editor failed");
        RemoveRule(programRules[0]);
        if (programRules.Count != 0) throw new InvalidOperationException("Program draft removal failed");
        CheckAppPicker(checks);
        // Доступность измерений зависит от источника узла и проверяется отдельно
        // в CheckPreviewControls и CheckServiceControls.
        if (!Routes.ValidateRulesButton.IsEnabled || backgroundWorkEnabled)
            throw new InvalidOperationException("Client block actions or fixture network isolation incorrect");
        checks.Add("Rule editor normalizes domains, cycles actions, answers «Куда пойдёт сайт?» and removes rules; background requests disabled in fixtures");
    }

    // Выбор программ: поиск по первым буквам имени, отметка, действие, «Добавить» — в правиле
    // имя файла, в списке правил — имя программы. Каталог подложный: файлы компьютера не читаются.
    private void CheckAppPicker(ICollection<string> checks)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Oblachka.exe");
        appCatalog = new[] { new InstalledApp("Telegram Desktop", "Telegram.exe", path, true), new InstalledApp("Steam", "steam.exe", path, false),
            new InstalledApp("Яндекс Музыка", "Яндекс Музыка.exe", path, false) }.Select(a => new Views.AppRow(a, null)).ToArray();
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
        var listed = Routes.ProgramList.Items.OfType<RuleRow>().Select(r => r.Text + "|" + r.Sub).ToArray();
        if (!listed.Contains("Telegram Desktop|Telegram.exe")) throw new InvalidOperationException("Rule list does not show program names: " + string.Join(", ", listed));
        OpenAppPicker();
        if (!appCatalog.Single(r => r.App.Executable == "Telegram.exe").Added) throw new InvalidOperationException("Added program is selectable again");
        CloseAppPicker();
        programRules.Clear(); RefreshRules(); appCatalog = null;
        checks.Add("Program picker finds apps by the first letters of any name word, adds several with one action and lists them by name");
    }
}

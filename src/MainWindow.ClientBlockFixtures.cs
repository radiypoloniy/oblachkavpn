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
        Routes.ProgramName.Text = "steam.exe";
        Routes.AddProgramButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        CycleRule(programRules[0]);
        if (programRules.Count != 1 || programRules[0].Action != SiteAction.Direct) throw new InvalidOperationException("Program draft editor failed");
        RemoveRule(programRules[0]);
        if (programRules.Count != 0) throw new InvalidOperationException("Program draft removal failed");
        // Доступность измерений зависит от источника узла и проверяется отдельно
        // в CheckPreviewControls и CheckServiceControls.
        if (!Routes.ValidateRulesButton.IsEnabled || backgroundWorkEnabled)
            throw new InvalidOperationException("Client block actions or fixture network isolation incorrect");
        checks.Add("Rule editor normalizes domains, cycles actions, answers «Куда пойдёт сайт?» and removes rules; background requests disabled in fixtures");
    }
}

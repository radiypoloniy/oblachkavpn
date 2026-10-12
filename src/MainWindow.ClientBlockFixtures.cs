using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckClientBlocks(ICollection<string> checks)
    {
        Routes.RuleDomain.Text = "Example.COM"; Routes.RuleAction.SelectedIndex = 2;
        Routes.AddRuleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (siteRules.Count != 1 || siteRules[0].Domain != "example.com" || siteRules[0].Action != SiteAction.Block)
            throw new InvalidOperationException("Rule editor did not normalize the draft");
        Routes.RuleList.SelectedIndex = 0; Routes.RemoveRuleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (siteRules.Count != 0) throw new InvalidOperationException("Draft removal failed");
        Routes.ProgramName.Text = "steam.exe"; Routes.ProgramAction.SelectedIndex = 1;
        Routes.AddProgramButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (programRules.Count != 1 || programRules[0].Action != SiteAction.Direct) throw new InvalidOperationException("Program draft editor failed");
        Routes.ProgramList.SelectedIndex = 0; Routes.RemoveProgramButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (programRules.Count != 0) throw new InvalidOperationException("Program draft removal failed");
        // Доступность измерений зависит от источника узла и проверяется отдельно
        // в CheckPreviewControls и CheckServiceControls.
        if (!Routes.ValidateRulesButton.IsEnabled || backgroundWorkEnabled)
            throw new InvalidOperationException("Client block actions or fixture network isolation incorrect");
        checks.Add("Rule editor normalizes draft domains and removes them; background requests disabled in fixtures");
    }
}

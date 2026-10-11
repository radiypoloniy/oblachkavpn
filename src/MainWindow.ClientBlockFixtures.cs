using System.Windows;

namespace RoutePilot;

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
        if (!Servers.MeasureButton.IsEnabled || !Servers.BestButton.IsEnabled || !Routes.ValidateRulesButton.IsEnabled || backgroundWorkEnabled)
            throw new InvalidOperationException("Client block actions or fixture network isolation incorrect");
        checks.Add("Rule editor normalizes draft domains and removes them; background requests disabled in fixtures");
    }
}

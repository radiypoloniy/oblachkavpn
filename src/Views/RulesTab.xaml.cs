using System.Windows;
using System.Windows.Controls;

namespace Oblachka.Views;

public partial class RulesTab : UserControl
{
    public RulesTab()
    {
        InitializeComponent();
        SitesTab.Click += (_, _) => ShowSites(true);
        AppsTab.Click += (_, _) => ShowSites(false);
        ShowSites(true);
    }

    // Подвкладки «Сайты / Программы»: активная подчёркнута акцентом.
    public void ShowSites(bool sites)
    {
        SiteInput.Visibility = RuleList.Visibility = sites ? Visibility.Visible : Visibility.Collapsed;
        ProgramInput.Visibility = ProgramList.Visibility = sites ? Visibility.Collapsed : Visibility.Visible;
        Mark(SitesTab, sites);
        Mark(AppsTab, !sites);
    }

    private static void Mark(Button tab, bool on)
    {
        tab.SetResourceReference(ForegroundProperty, on ? "Tx" : "Mu2");
        tab.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        tab.SetResourceReference(BorderBrushProperty, "Ac");
        tab.BorderThickness = new Thickness(0, 0, 0, on ? 2 : 0);
    }
}

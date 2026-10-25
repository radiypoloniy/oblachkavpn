using System.Windows;
using System.Windows.Controls;

namespace Oblachka.Views;

public partial class RulesTab : UserControl
{
    public RulesTab()
    {
        InitializeComponent();
        // Узкий остров: у «Импорт» и «Экспорт» — только значки, иначе заголовок сжимался до «Прав…».
        SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 560;
            ImportText.Visibility = ExportText.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            ImportRules.Padding = ExportRules.Padding = narrow ? new Thickness(10, 0, 4, 0) : new Thickness(12, 0, 14, 0);
        };
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

using System.Windows;
using System.Windows.Controls;

namespace Oblachka.Views;

public partial class RulesTab : UserControl
{
    public bool ServicesActive => ServicePanel.Visibility == Visibility.Visible;
    public event Action? SectionChanged;
    internal TextBox RuleDomain => Quick.Query;
    internal Button AddRuleButton => Quick.AddVpn;
    private Button activeTab = null!;

    public RulesTab()
    {
        InitializeComponent();
        // Узкий остров: у «Импорт» и «Экспорт» — только значки, иначе заголовок сжимался до «Прав…».
        SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 560;
            ImportText.Visibility = ExportText.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            var serverSlot = narrow ? CompactServerSlot : InlineServerSlot;
            if (RuleSetServers.Parent is Panel previous && previous != serverSlot)
            {
                previous.Children.Remove(RuleSetServers);
                serverSlot.Children.Add(RuleSetServers);
            }
            CompactServerSlot.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            RuleSetText.MaxWidth = narrow ? 120 : 180;
            ImportRules.Padding = ExportRules.Padding = narrow ? new Thickness(10, 0, 4, 0) : new Thickness(12, 0, 14, 0);
        };
        SitesTab.Click += (_, _) => ShowSites(true);
        AppsTab.Click += (_, _) => ShowSites(false);
        ServicesTab.Click += (_, _) => ShowServices();
        ShowSites(true);
    }

    // Подвкладки «Сайты / Программы»: активная подчёркнута акцентом.
    public void ShowSites(bool sites)
    {
        ServicePanel.Visibility = Visibility.Collapsed;
        ImportActions.Visibility = RuleEditor.Visibility = Visibility.Visible;
        Mark(ServicesTab, false);
        RuleList.Visibility = sites ? Visibility.Visible : Visibility.Collapsed;
        ProgramInput.Visibility = ProgramList.Visibility = sites ? Visibility.Collapsed : Visibility.Visible;
        Mark(SitesTab, sites);
        Mark(AppsTab, !sites);
        activeTab = sites ? SitesTab : AppsTab;
        SectionChanged?.Invoke();
    }

    public void ShowServices()
    {
        ImportActions.Visibility = RuleEditor.Visibility = Visibility.Collapsed;
        ServicePanel.Visibility = Visibility.Visible;
        Mark(SitesTab, false); Mark(AppsTab, false); Mark(ServicesTab, true);
        activeTab = ServicesTab;
        SectionChanged?.Invoke();
    }

    public void FocusActiveTab() => activeTab.Focus();

    private static void Mark(Button tab, bool on)
    {
        tab.SetResourceReference(ForegroundProperty, on ? "Tx" : "Mu2");
        tab.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        tab.SetResourceReference(BorderBrushProperty, "Ac");
        tab.BorderThickness = new Thickness(0, 0, 0, on ? 2 : 0);
    }
}

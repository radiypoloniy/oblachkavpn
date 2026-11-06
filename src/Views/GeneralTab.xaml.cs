using System.Windows;
using System.Windows.Controls;

namespace Oblachka.Views;

public partial class GeneralTab : UserControl
{
    public GeneralTab() => InitializeComponent();

    internal void ShowSection(int page)
    {
        LaunchSection.Visibility = page == SettingsPanel.LaunchPage ? Visibility.Visible : Visibility.Collapsed;
        NetworkSection.Visibility = page == SettingsPanel.NetworkPage ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = page == SettingsPanel.AppearancePage ? Visibility.Visible : Visibility.Collapsed;
        TransferSection.Visibility = page == SettingsPanel.TransferPage ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
    }
}

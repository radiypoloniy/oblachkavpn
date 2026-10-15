using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Oblachka.Views;

// Настройки нового дизайна. Свойства ниже — прежние имена элементов настроек: логика окна
// обращается к ним как раньше (Settings.SystemTunnel и т. п.), а живут они во вкладках.
public partial class SettingsPanel : UserControl
{
    public SettingsPanel()
    {
        InitializeComponent();
        SettingsTabs.SelectionChanged += (_, _) => ShowTab(SettingsTabs.SelectedIndex);
    }

    public CheckBox SystemTunnel => General.SystemTunnel;
    public CheckBox SystemProxy => General.SystemProxy;
    public TextBlock SystemTunnelHint => General.SystemTunnelHint;
    public TextBlock SystemTunnelNote => General.SystemTunnelNote;
    public Button RestoreNetwork => General.RestoreNetwork;
    public CheckBox AutoRecover => General.AutoRecover;
    public CheckBox StartWithWindows => General.StartWithWindows;
    public CheckBox StayInTray => General.StayInTray;
    public CheckBox AutoRefresh => Subscription.AutoRefresh;
    public TextBlock StorageStatus => Subscription.StorageStatus;
    public TextBlock SubscriptionDetails => Subscription.SubscriptionDetails;
    public TextBlock DeviceIdText => Subscription.DeviceIdText;
    public Button AddExternalButton => Subscription.AddExternalButton;
    public TextBlock VersionText => Help.VersionText;

    // Смена вкладки: сдвиг 4 → 0 и проявление за 200 мс.
    public void ShowTab(int index)
    {
        if (SettingsTabs.SelectedIndex != index) { SettingsTabs.SelectedIndex = index; return; }
        UIElement[] pages = { General, Rules, Subscription, Help };
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        var page = pages[Math.Clamp(index, 0, 3)];
        page.RenderTransform = new TranslateTransform();
        page.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(200)));
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }
}

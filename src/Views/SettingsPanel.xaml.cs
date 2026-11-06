using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Oblachka.Views;

public partial class SettingsPanel : UserControl
{
    public const int ConnectionPage = 0, BypassPage = 1, SubscriptionPage = 2, AppPage = 3, HelpPage = 4, AutoPoolPage = 5, AdvancedPage = 6;
    public const int LaunchPage = 7, NetworkPage = 8, AppearancePage = 9, TransferPage = 10;
    private static readonly string[] Titles = ["Настройки", "Обход блокировок", "Подписки и ключ", "Настройки", "Помощь",
        "Серверы автовыбора", "Тонкая настройка", "Запуск", "Сети и защита", "Вид и уведомления", "Обновления и перенос"];

    public SettingsPanel() => InitializeComponent();

    // Разделы, где теперь живут бывшие настройки «Подключения»: задаёт окно до первой подписки на события.
    public ConnectionsView LiveView { get; set; } = null!;
    public ServersSheet ServersView { get; set; } = null!;

    public int CurrentPage { get; private set; } = AppPage;
    public bool FromHub { get; private set; }

    public CheckBox SystemTunnel => General.SystemTunnel;
    public CheckBox SystemProxy => General.SystemProxy;
    public CheckBox RussiaDirect => Bypass.RussiaDirect;
    public TextBlock SystemTunnelHint => LiveView.SystemTunnelHint;
    public TextBlock SystemTunnelNote => Help.SystemTunnelNote;
    public Button RestoreNetwork => Help.RestoreNetwork;
    public CheckBox AutoRecover => ServersView.AutoRecover;
    public CheckBox StartWithWindows => General.StartWithWindows;
    public CheckBox ConnectOnLaunch => General.ConnectOnLaunch;
    public CheckBox LocalNetwork => LiveView.LocalNetwork;
    public CheckBox QuicOverTcp => Help.QuicOverTcp;
    public CheckBox CompatibleStack => Help.CompatibleStack;
    public CheckBox FreeServers => Subscription.FreeServers;
    public CheckBox StayInTray => General.StayInTray;
    public CheckBox Hotkey => General.Hotkey;
    public CheckBox Notices => General.Notices;
    public TextBlock HotkeyNote => General.HotkeyNote;
    public Button BackupSave => General.BackupSave;
    public Button BackupRestore => General.BackupRestore;
    public AccessibleItems AutoPoolList => AutoPool.AutoPoolList;
    public CheckBox AutoRefresh => Subscription.AutoRefresh;
    public TextBlock StorageStatus => Subscription.StorageStatus;
    public TextBlock DeviceIdText => Subscription.DeviceIdText;
    public Button AddExternalButton => Subscription.AddExternalButton;
    public TextBlock VersionText => General.VersionText;

    public void ShowHome() => ShowTab(AppPage);

    public bool Back()
    {
        if (!FromHub) return false;
        ShowHome();
        return true;
    }

    public void ShowTab(int index, bool fromHub = false)
    {
        UIElement[] pages = [Hub, Bypass, Subscription, Hub, Help, AutoPool, Advanced, General, General, General, General];
        index = Math.Clamp(index, 0, Titles.Length - 1);
        CurrentPage = index;
        FromHub = fromHub;
        var page = pages[index];
        if (ReferenceEquals(page, General)) General.ShowSection(index);
        if (ReferenceEquals(page, Hub)) RefreshLaunchSummary();
        foreach (var each in pages.Distinct()) each.Visibility = ReferenceEquals(each, page) ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = Titles[index];
        page.RenderTransform = new TranslateTransform();
        page.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(200)));
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    internal void RefreshLaunchSummary()
    {
        var launch = StartWithWindows.IsChecked == true
            ? "Облачка запускается вместе с Windows." : "Вы открываете Облачку вручную.";
        var resume = ConnectOnLaunch.IsChecked == true
            ? " VPN восстановится, если был включён." : " VPN подключается вручную.";
        Hub.LaunchSummary.Text = launch + resume;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Oblachka.Views;

// Настройки нового дизайна. Свойства ниже — прежние имена элементов настроек: логика окна
// обращается к ним как раньше (Settings.SystemTunnel и т. п.), а живут они на страницах.
// Страницы: 0 — корневая, 1 — «Без VPN», 2 — «Подписки и ключ», 3 — «Помощь»,
// 4 — «Автовыбор», 5 — «Для продвинутых».
public partial class SettingsPanel : UserControl
{
    private static readonly string[] Titles = { "Настройки", "Без VPN", "Подписки и ключ", "Помощь", "Автовыбор", "Для продвинутых" };

    public SettingsPanel()
    {
        InitializeComponent();
        General.OpenZapret.Click += (_, _) => ShowTab(1);
        General.OpenRules.Click += (_, _) => ShowTab(1);
        General.OpenAutoPool.Click += (_, _) => ShowTab(4);
        General.OpenVersion.Click += (_, _) => ShowTab(3);
        General.OpenSubscription.Click += (_, _) => ShowTab(2);
        General.OpenHelp.Click += (_, _) => ShowTab(3);
        General.OpenAdvanced.Click += (_, _) => ShowTab(5);
    }

    public int CurrentPage { get; private set; }

    public CheckBox SystemTunnel => General.SystemTunnel;
    public CheckBox SystemProxy => General.SystemProxy;
    public CheckBox RussiaDirect => General.RussiaDirect;
    public TextBlock SystemTunnelHint => Advanced.SystemTunnelHint;
    public TextBlock SystemTunnelNote => Advanced.SystemTunnelNote;
    public Button RestoreNetwork => Advanced.RestoreNetwork;
    public CheckBox AutoRecover => General.AutoRecover;
    public CheckBox StartWithWindows => General.StartWithWindows;
    public CheckBox ConnectOnLaunch => General.ConnectOnLaunch;
    public CheckBox StayInTray => Advanced.StayInTray;
    public StackPanel AutoPoolList => AutoPool.AutoPoolList;
    public CheckBox AutoRefresh => Subscription.AutoRefresh;
    public TextBlock StorageStatus => Subscription.StorageStatus;
    public TextBlock SubscriptionDetails => Subscription.SubscriptionDetails;
    public TextBlock DeviceIdText => Subscription.DeviceIdText;
    public Button AddExternalButton => Subscription.AddExternalButton;
    public TextBlock VersionText => Help.VersionText;

    // Подробная страница → корневая; корневая — false: закрыть настройки.
    public bool Back()
    {
        if (CurrentPage == 0) return false;
        ShowTab(0);
        return true;
    }

    // Смена страницы: сдвиг 4 → 0 и проявление за 200 мс.
    public void ShowTab(int index)
    {
        UIElement[] pages = { General, Rules, Subscription, Help, AutoPool, Advanced };
        index = Math.Clamp(index, 0, pages.Length - 1);
        CurrentPage = index;
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = Titles[index];
        var page = pages[index];
        page.RenderTransform = new TranslateTransform();
        page.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(200)));
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }
}

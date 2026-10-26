using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Oblachka.Views;

// Страницы острова облика 0.20. Свойства ниже — прежние имена элементов настроек: логика окна
// обращается к ним как раньше (Settings.SystemTunnel и т. п.), а живут они там, где их ищут:
// режим и локальная сеть — в «Программах и сайтах» (LiveView), переключение при обрыве — в
// «Серверах» (ServersView), средства от бед — в «Помощи», запуск — в «Приложении».
// Номера страниц прежние: 0 и 3 — «Приложение», 1 — «Обход блокировок», 2 — «Подписки и ключ»,
// 4 — «Помощь», 5 — «Серверы автовыбора», 6 — «Для продвинутых».
public partial class SettingsPanel : UserControl
{
    public const int ConnectionPage = 0, BypassPage = 1, SubscriptionPage = 2, AppPage = 3, HelpPage = 4, AutoPoolPage = 5, AdvancedPage = 6;
    private static readonly string[] Titles = { "Приложение", "Обход блокировок", "Подписки и ключ", "Приложение", "Помощь", "Серверы автовыбора", "Для продвинутых" };

    public SettingsPanel() => InitializeComponent();

    // Разделы, где теперь живут бывшие настройки «Подключения»: задаёт окно до первой подписки на события.
    public ConnectionsView LiveView { get; set; } = null!;
    public ServersSheet ServersView { get; set; } = null!;

    public int CurrentPage { get; private set; } = AppPage;

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
    public StackPanel AutoPoolList => AutoPool.AutoPoolList;
    public CheckBox AutoRefresh => Subscription.AutoRefresh;
    public TextBlock StorageStatus => Subscription.StorageStatus;
    public TextBlock SubscriptionDetails => Subscription.SubscriptionDetails;
    public TextBlock DeviceIdText => Subscription.DeviceIdText;
    public Button AddExternalButton => Subscription.AddExternalButton;
    public TextBlock VersionText => General.VersionText;

    // Открытие без выбранной страницы — «Приложение».
    public void ShowHome() => ShowTab(AppPage);

    // Списка разделов нет: «Назад» всегда закрывает страницу.
    public bool Back() => false;

    // Смена страницы: сдвиг 4 → 0 и проявление за 200 мс.
    public void ShowTab(int index)
    {
        UIElement[] pages = { General, Bypass, Subscription, General, Help, AutoPool, Advanced };
        index = Math.Clamp(index, 0, pages.Length - 1);
        CurrentPage = index;
        var page = pages[index];
        foreach (var each in pages.Distinct()) each.Visibility = ReferenceEquals(each, page) ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = Titles[index];
        page.RenderTransform = new TranslateTransform();
        page.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(200)));
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }
}

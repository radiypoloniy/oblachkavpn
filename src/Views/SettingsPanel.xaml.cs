using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Oblachka.Views;

// Настройки нового дизайна. Свойства ниже — прежние имена элементов настроек: логика окна
// обращается к ним как раньше (Settings.SystemTunnel и т. п.), а живут они на страницах.
// Страницы: 0 — «Подключение», 1 — «Обход блокировок», 2 — «Подписка», 3 — «Приложение»,
// 4 — «Помощь», 5 — «Автовыбор» (внутри «Подключения»).
public partial class SettingsPanel : UserControl
{
    public const int ConnectionPage = 0, BypassPage = 1, SubscriptionPage = 2, AppPage = 3, HelpPage = 4, AutoPoolPage = 5;
    private static readonly string[] Titles = { "Подключение", "Обход блокировок", "Подписка", "Приложение", "Помощь", "Автовыбор" };
    // Уже этой ширины разделы и страница не помещаются рядом: сначала список, потом страница.
    private const double CompactWidth = 620;
    private bool compact;

    public SettingsPanel()
    {
        InitializeComponent();
        var categories = new[] { CatConnection, CatBypass, CatSubscription, CatApp, CatHelp };
        for (var i = 0; i < categories.Length; i++)
        {
            var index = i;
            categories[i].Click += (_, _) => ShowTab(index);
        }
        Advanced.OpenAutoPool.Click += (_, _) => ShowTab(AutoPoolPage);
        SizeChanged += (_, e) =>
        {
            var now = e.NewSize.Width < CompactWidth;
            if (now == compact) return;
            compact = now;
            Arrange();
        };
    }

    // -1 — список разделов (только в узком острове).
    public int CurrentPage { get; private set; }

    public CheckBox SystemTunnel => General.SystemTunnel;
    public CheckBox SystemProxy => General.SystemProxy;
    public CheckBox RussiaDirect => Bypass.RussiaDirect;
    public TextBlock SystemTunnelHint => Advanced.SystemTunnelHint;
    public TextBlock SystemTunnelNote => Advanced.SystemTunnelNote;
    public Button RestoreNetwork => Advanced.RestoreNetwork;
    public CheckBox AutoRecover => Advanced.AutoRecover;
    public CheckBox StartWithWindows => Advanced.StartWithWindows;
    public CheckBox ConnectOnLaunch => Advanced.ConnectOnLaunch;
    public CheckBox LocalNetwork => Advanced.LocalNetwork;
    public CheckBox QuicOverTcp => Advanced.QuicOverTcp;
    public CheckBox StayInTray => General.StayInTray;
    public CheckBox Hotkey => General.Hotkey;
    public TextBlock HotkeyNote => General.HotkeyNote;
    public StackPanel AutoPoolList => AutoPool.AutoPoolList;
    public CheckBox AutoRefresh => Subscription.AutoRefresh;
    public TextBlock StorageStatus => Subscription.StorageStatus;
    public TextBlock SubscriptionDetails => Subscription.SubscriptionDetails;
    public TextBlock DeviceIdText => Subscription.DeviceIdText;
    public Button AddExternalButton => Subscription.AddExternalButton;
    public TextBlock VersionText => General.VersionText;

    // Открытие настроек без выбранного раздела: в узком острове — список, в широком — «Подключение».
    public void ShowHome()
    {
        if (compact) { CurrentPage = -1; Arrange(); }
        else ShowTab(ConnectionPage);
    }

    // «Назад»: «Автовыбор» → «Подключение», страница узкого острова → список; иначе false — закрыть.
    public bool Back()
    {
        if (CurrentPage == AutoPoolPage) { ShowTab(ConnectionPage); return true; }
        if (compact && CurrentPage >= 0) { CurrentPage = -1; Arrange(); return true; }
        return false;
    }

    // Смена страницы: сдвиг 4 → 0 и проявление за 200 мс.
    public void ShowTab(int index)
    {
        UIElement[] pages = { Advanced, Bypass, Subscription, General, Help, AutoPool };
        index = Math.Clamp(index, 0, pages.Length - 1);
        CurrentPage = index;
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        Arrange();
        var page = pages[index];
        page.RenderTransform = new TranslateTransform();
        page.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(200)));
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    // Широко: разделы слева всегда, заголовок «Настройки». Узко: либо список, либо страница с её именем.
    private void Arrange()
    {
        var list = CurrentPage < 0;
        if (list && !compact) { ShowTab(ConnectionPage); return; }
        ListColumn.Width = compact ? (list ? new GridLength(1, GridUnitType.Star) : new GridLength(0)) : new GridLength(220);
        Categories.Visibility = compact && !list ? Visibility.Collapsed : Visibility.Visible;
        Pages.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = list || !compact && CurrentPage != AutoPoolPage ? "Настройки" : Titles[CurrentPage];
        // Подсветка раздела: «Автовыбор» — часть «Подключения».
        var selected = CurrentPage == AutoPoolPage ? ConnectionPage : CurrentPage;
        var categories = new[] { CatConnection, CatBypass, CatSubscription, CatApp, CatHelp };
        for (var i = 0; i < categories.Length; i++) Oblachka.Ui.SetSelected(categories[i], !compact && i == selected);
    }
}

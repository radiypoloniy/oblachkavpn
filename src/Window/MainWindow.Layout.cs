using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;

namespace Oblachka;

// Раскладка окна 0.16: боковая панель (Home) + остров с разделами. По умолчанию на острове
// «Сейчас в сети»; «Серверы» и настройки открываются на нём же. Узкое окно (меньше 760 px) —
// только боковая панель, раздел открывается островом поверх неё и закрывается «назад».
public partial class MainWindow
{
    private const double NarrowWidth = 760;
    private bool narrow;
    // В узком окне остров виден, только когда человек открыл раздел.
    private bool narrowOpen;

    private void WireLayout()
    {
        // Ширина — от корневой сетки: она разложена под настоящий размер и в снимке окна без показа.
        Shell.SizeChanged += (_, _) => ApplyLayout();
        // Скругление острова — как у неба. Border не обрезает содержимое, а у разделов свой
        // квадратный фон: без обрезки он закрывал углы.
        IslandHost.SizeChanged += (_, e) => IslandHost.Clip = new RectangleGeometry(new Rect(e.NewSize), 17, 17);
        StateChanged += (_, _) =>
        {
            // Развёрнутое окно без системной рамки уходит краями за экран на толщину рамки.
            Shell.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaximizeButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть");
            Home.Sky.Sync();
        };
        IsVisibleChanged += (_, _) => Home.Sky.Sync();
        // Любой экран острова открылся или закрылся — пересчитать раскладку и подсветку пункта.
        foreach (var page in new UIElement[] { Live, Servers, Settings, Picker, Doctor, SiteCheck })
            page.IsVisibleChanged += (_, _) => { if (IsLoaded) ApplyLayout(); };
    }

    private void MaximizeClick(object sender, RoutedEventArgs args) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Раздел открыт явно (в узком окне — поверх панели).
    private void OpenSection() { narrowOpen = true; ApplyLayout(); }

    // Остров без явного раздела: в широком окне — «Сейчас в сети», в узком — скрыт.
    private void ShowDefaultSection()
    {
        narrowOpen = false;
        if (!narrow && Onboarding.Visibility != Visibility.Visible) { if (Live.Visibility != Visibility.Visible) OpenConnections(focus: false); }
        else CloseConnections();
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (!viewsReady) return;
        var wasNarrow = narrow;
        narrow = Shell.ActualWidth > 0 && Shell.ActualWidth < NarrowWidth;
        SideColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(276);
        IslandColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(Island, narrow ? 0 : 1);
        Grid.SetColumnSpan(Island, narrow ? 2 : 1);
        Island.Margin = narrow ? new Thickness(10, 44, 10, 10) : new Thickness(0, 44, 10, 10);
        var welcome = Onboarding.Visibility == Visibility.Visible;
        var anyOpen = Live.Visibility == Visibility.Visible || Servers.Visibility == Visibility.Visible || Settings.Visibility == Visibility.Visible ||
            Picker.Visibility == Visibility.Visible || Doctor.Visibility == Visibility.Visible || SiteCheck.Visibility == Visibility.Visible;
        Island.Visibility = welcome || narrow && !(narrowOpen && anyOpen) ? Visibility.Collapsed : Visibility.Visible;
        // «Назад» у разделов нужен только в узком окне: в широком раздел сменяют пунктом панели.
        Live.BackButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        Servers.BackButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        // Окно расширили: пустой остров заполняется «Сейчас в сети».
        if (wasNarrow && !narrow && !anyOpen && !welcome) { OpenConnections(focus: false); return; }
        RefreshSideSelection();
    }

    // Подсветка пункта боковой панели — того раздела, что сейчас на острове.
    private void RefreshSideSelection()
    {
        var island = Island.Visibility == Visibility.Visible;
        // Поверх раздела могут лежать «Не открывается сайт?», «Сеть Windows», выбор программ:
        // тогда ни один пункт не подсвечен, кроме того, из чьих настроек их открыли.
        var page = Picker.Visibility == Visibility.Visible || Doctor.Visibility == Visibility.Visible || SiteCheck.Visibility == Visibility.Visible;
        var settings = island && Settings.Visibility == Visibility.Visible;
        var rules = settings && Settings.CurrentPage == 1;
        Ui.SetSelected(Home.LiveNav, island && !page && Live.Visibility == Visibility.Visible && !settings && Servers.Visibility != Visibility.Visible);
        Ui.SetSelected(Home.AllServersButton, island && Servers.Visibility == Visibility.Visible && !settings);
        Ui.SetSelected(Home.RulesNav, rules);
        Ui.SetSelected(Home.SettingsButton, settings && !rules);
    }
}

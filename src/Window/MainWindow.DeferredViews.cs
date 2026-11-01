using System.Windows;
using Oblachka.Views;

namespace Oblachka;

public partial class MainWindow
{
    private AppPicker? picker;
    private NetworkDoctorView? doctor;
    private SiteCheckView? siteCheck;
    private AnalyticsView? analytics;
    private AppPicker Picker => DeferredPage(ref picker, WirePickerPage);
    private NetworkDoctorView Doctor => DeferredPage(ref doctor, WireDoctorPage);
    private SiteCheckView SiteCheck => DeferredPage(ref siteCheck, WireSiteCheckPage);
    private AnalyticsView Analytics => DeferredPage(ref analytics, WireAnalyticsPage);

    // Скрытие и проверка видимости не должны создавать ещё не открывавшийся экран.
    private static void HidePage(UIElement? page) { if (page is not null) page.Visibility = Visibility.Collapsed; }

    private T DeferredPage<T>(ref T? page, Action wire) where T : UIElement, new()
    {
        if (page is not null) return page;
        var created = new T { Visibility = Visibility.Collapsed };
        page = created;
        IslandHost.Children.Add(created);
        created.IsVisibleChanged += (_, _) => { if (IsLoaded) ApplyLayout(); };
        wire();
        ApplyLayout();
        return created;
    }
}

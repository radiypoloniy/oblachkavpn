using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Oblachka;

public partial class MainWindow
{
    private sealed record ServiceRouteRow(string Name, string Server, string Targets, bool Missing)
    {
        public override string ToString() => Name;
    }

    private ServiceRoute[] displayedServiceRoutes = [];
    private IReadOnlyList<Profile>? displayedServiceProfiles;

    private void WireServiceRoutes()
    {
        Home.ServiceSummary.AllRoutes.Click += (_, _) =>
        {
            ShowRulesSection(services: true);
            Routes.ServiceList.SelectedIndex = -1;
        };
        Home.ServiceSummary.QuickRoutes.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, args) =>
        {
            if (args.OriginalSource is not Button { DataContext: ServiceRouteRow route }) return;
            ShowRulesSection(services: true);
            Routes.ServiceList.SelectedValue = route.Name;
            if (Routes.ServiceList.SelectedItem is { } selected) Routes.ServiceList.ScrollIntoView(selected);
        }));
    }

    private void RefreshServiceRoutes()
    {
        var routes = runtime.TunnelPolicy.ServiceRoutes;
        if (ReferenceEquals(displayedServiceProfiles, importedProfiles) && displayedServiceRoutes.SequenceEqual(routes)) return;
        displayedServiceProfiles = importedProfiles;
        displayedServiceRoutes = routes.ToArray();
        var selected = (Routes.ServiceList.SelectedItem as ServiceRouteRow)?.Name;
        var rows = routes.Select(route =>
        {
            var server = ClientServerCatalog.Find(runtime, route.ProfileId);
            return new ServiceRouteRow(route.Id,
                server is null ? "Сервер отсутствует" : ClientServerCatalog.Title(server),
                string.Join(", ", route.Domains.Concat(route.Files.Select(file => file.Executable))), server is null);
        }).ToArray();
        Routes.ServiceEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Routes.ServiceList.ItemsSource = rows;
        Routes.ServiceList.SelectedItem = rows.FirstOrDefault(row => row.Name == selected);
        Home.ServiceSummary.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Home.ServiceSummary.RouteCount.Text = rows.Length.ToString();
        Home.ServiceSummary.QuickRoutes.ItemsSource = rows.OrderByDescending(row => row.Missing).Take(2).ToArray();
    }
}

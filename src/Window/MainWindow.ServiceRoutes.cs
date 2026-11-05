using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Oblachka;

public partial class MainWindow
{
    private sealed record ServiceRouteRow(string Id, string Name, string Server, string Targets, string Status, bool Warning)
    {
        public override string ToString() => Name;
    }

    private ServiceRoute[] displayedServiceRoutes = [];
    private (string Text, bool Warning)[] displayedServiceStatus = [];
    private IReadOnlyList<Profile>? displayedServiceProfiles;
    private sealed record ServiceServer(string Name, Profile Profile);
    private ProgramRule[] serviceFiles = [];
    private bool serviceEditorOpened;

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
            Routes.ServiceList.SelectedValue = route.Id;
            if (Routes.ServiceList.SelectedItem is { } selected) Routes.ServiceList.ScrollIntoView(selected);
        }));
        Routes.ServicePresetPicker.ItemsSource = ClientServiceRoutes.Presets;
        Routes.ServicePresetPicker.SelectionChanged += (_, _) => LoadServiceDraft();
        Routes.ServiceList.SelectionChanged += (_, _) =>
        {
            if (Routes.ServiceList.SelectedItem is ServiceRouteRow row)
                Routes.ServicePresetPicker.SelectedItem = ClientServiceRoutes.Presets.FirstOrDefault(p => p.Id == row.Id);
        };
        Routes.ServiceServerPicker.DropDownOpened += (_, _) => LoadServiceServers();
        Routes.ServiceFiles.Click += (_, _) => PickServiceFiles();
        Routes.ServiceClearFiles.Click += (_, _) => { serviceFiles = []; ShowServiceFiles(); };
        Routes.ServiceSave.Click += (_, _) => SaveServiceRoute();
        Routes.ServiceDelete.Click += (_, _) =>
        {
            var id = (Routes.ServicePresetPicker.SelectedItem as ServicePreset)?.Id ?? (Routes.ServiceList.SelectedItem as ServiceRouteRow)?.Id;
            if (id is null) return;
            runtime.ServiceRoutes.Remove(id);
            LoadServiceDraft();
            RefreshServiceRoutes();
        };
        Routes.ServiceApply.Click += async (_, _) => await ApplyServiceRoutesAsync();
    }

    private void OpenServiceEditor()
    {
        if (serviceEditorOpened) return;
        serviceEditorOpened = true;
        Routes.ServicePresetPicker.SelectedIndex = 0;
    }

    private void RefreshServiceRoutes()
    {
        var routes = runtime.TunnelPolicy.ServiceRoutes;
        var pending = runtime.ServiceRoutes.HasPendingChanges;
        Routes.ServiceApply.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        Routes.ServiceApplyNote.Text = pending ? "Изменения сохранены. Для применения нужно переподключить VPN."
            : !runtime.Preferences.SystemTunnel ? "Маршруты действуют в режиме «Весь компьютер»."
            : runtime.Tunnel is null ? "Маршруты применятся при следующем подключении." : "";
        var status = routes.Select(runtime.ServiceRoutes.Status).ToArray();
        if (ReferenceEquals(displayedServiceProfiles, importedProfiles) && displayedServiceRoutes.SequenceEqual(routes) &&
            displayedServiceStatus.SequenceEqual(status)) return;
        displayedServiceProfiles = importedProfiles;
        displayedServiceRoutes = routes.ToArray();
        displayedServiceStatus = status;
        var selected = (Routes.ServiceList.SelectedItem as ServiceRouteRow)?.Id;
        var rows = routes.Select((route, index) =>
        {
            var server = ClientServerCatalog.Find(runtime, route.ProfileId);
            return new ServiceRouteRow(route.Id, ClientServiceRoutes.Title(route.Id),
                server is null ? "Сервер отсутствует" : ClientServerCatalog.Title(server),
                string.Join(", ", route.Domains.Concat(route.Files.Select(file => file.Executable))), status[index].Text, status[index].Warning);
        }).ToArray();
        Routes.ServiceEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Routes.ServiceList.ItemsSource = rows;
        Routes.ServiceList.SelectedItem = rows.FirstOrDefault(row => row.Id == selected);
        Home.ServiceSummary.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Home.ServiceSummary.RouteCount.Text = rows.Length.ToString();
        Home.ServiceSummary.QuickRoutes.ItemsSource = rows.OrderByDescending(row => row.Warning).Take(2).ToArray();
    }

    private void LoadServiceDraft()
    {
        var preset = Routes.ServicePresetPicker.SelectedItem as ServicePreset;
        var saved = runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == preset?.Id);
        serviceFiles = saved?.Files ?? [];
        LoadServiceServers(saved?.ProfileId, preserveSelection: false);
        ShowServiceFiles();
    }

    private void LoadServiceServers(string? id = null, bool preserveSelection = true)
    {
        if (preserveSelection && Routes.ServiceServerPicker.SelectedItem is ServiceServer selected)
            id = ClientServerCatalog.Id(selected.Profile);
        else if (preserveSelection && Routes.ServicePresetPicker.SelectedItem is ServicePreset preset)
            id = runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == preset.Id)?.ProfileId;
        var servers = importedProfiles.Where(runtime.Tunnels.ServiceProfileAllowed)
            .Select(p => new ServiceServer(ClientServerCatalog.Title(p), p)).ToArray();
        Routes.ServiceServerPicker.ItemsSource = servers;
        Routes.ServiceServerPicker.SelectedItem = id is null ? null :
            servers.FirstOrDefault(s => string.Equals(ClientServerCatalog.Id(s.Profile), id, StringComparison.OrdinalIgnoreCase));
    }

    private void PickServiceFiles()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            serviceFiles = picker.FileNames.Select(path => ProgramRoutingPlan.Create(path, SiteAction.Tunnel, allInFolder: false)).ToArray();
            ShowServiceFiles();
        }
        catch (UserError error) { MessageBox.Show(this, error.Message, "Маршрутизация"); }
    }

    private void ShowServiceFiles() => Routes.ServiceFileNames.Text = string.Join(", ", serviceFiles.Select(f => f.Executable));

    private void SaveServiceRoute()
    {
        if (Routes.ServicePresetPicker.SelectedItem is not ServicePreset preset ||
            Routes.ServiceServerPicker.SelectedItem is not ServiceServer server)
        {
            MessageBox.Show(this, "Выберите сервис и VPN-сервер.", "Маршрутизация");
            return;
        }
        try
        {
            var draft = runtime.ServiceRoutes.Draft(preset, server.Profile, serviceFiles);
            var conflicts = runtime.ServiceRoutes.Conflicts(draft);
            if (conflicts.Length > 0 && MessageBox.Show(this,
                "Эти правила могут иметь приоритет над маршрутом сервиса:\n\n" +
                string.Join("\n", conflicts.Take(6)) + (conflicts.Length > 6 ? $"\nЕщё правил: {conflicts.Length - 6}" : "") +
                "\n\nСохранить привязку?", "Пересечение правил", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            runtime.ServiceRoutes.Save(draft);
            RefreshServiceRoutes();
            Routes.ServiceList.SelectedValue = draft.Id;
        }
        catch (UserError error) { MessageBox.Show(this, error.Message, "Маршрутизация"); }
    }

    private async Task ApplyServiceRoutesAsync()
    {
        if (!runtime.ServiceRoutes.HasPendingChanges || runtime.Profiles.Running is not { } running) return;
        if (MessageBox.Show(this, "Соединения прервутся, а защита VPN на время переподключения снимется. Продолжить?",
            "Переподключение VPN", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { await runtime.Connections.RestartAsync(running, rebuildTunnel: true); }
        catch (UserError error) { MessageBox.Show(this, error.Message, "Маршрутизация"); }
        if (!closed) RefreshServiceRoutes();
    }
}

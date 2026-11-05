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
    private sealed record ServiceServer(string Name, Profile Profile)
    {
        public override string ToString() => Name;
    }
    private ProgramRule[] serviceFiles = [];
    private bool serviceEditorOpened;
    private ServiceRoute? customServiceDraft;

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
        Routes.ServicePresetPicker.SelectionChanged += (_, _) =>
        {
            if (Routes.ServicePresetPicker.SelectedItem is not ServicePreset) return;
            customServiceDraft = null;
            LoadServiceDraft();
        };
        Routes.ServiceList.SelectionChanged += (_, _) =>
        {
            if (Routes.ServiceList.SelectedItem is not ServiceRouteRow row) return;
            if (ClientServiceRoutes.Presets.FirstOrDefault(p => p.Id == row.Id) is { } preset)
                Routes.ServicePresetPicker.SelectedItem = preset;
            else if (customServiceDraft?.Id != row.Id && runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == row.Id) is { } route)
                LoadCustomServiceDraft(route.Id, route.Domains, route.Files);
        };
        Routes.ServiceServerPicker.DropDownOpened += (_, _) => LoadServiceServers();
        Routes.ServiceFiles.Click += (_, _) => PickServiceFiles();
        Routes.ServiceClearFiles.Click += (_, _) => { serviceFiles = []; ShowServiceFiles(); };
        Routes.ServiceSave.Click += (_, _) => SaveServiceRoute();
        Routes.ServiceDelete.Click += (_, _) =>
        {
            var id = customServiceDraft?.Id ?? (Routes.ServicePresetPicker.SelectedItem as ServicePreset)?.Id;
            if (id is null) return;
            runtime.ServiceRoutes.Remove(id);
            customServiceDraft = null;
            Routes.ServicePresetPicker.SelectedIndex = 0;
            LoadServiceDraft();
            RefreshServiceRoutes();
        };
        Routes.ServiceApply.Click += async (_, _) => await ApplyServiceRoutesAsync();
        Routes.ServiceSuggestionAdd.Click += (_, _) =>
        {
            var add = serviceSuggestion;
            ClearServiceSuggestion();
            try { add?.Invoke(); }
            catch (UserError error) { DetailText.Text = error.Message; }
        };
        Routes.ServiceSuggestionDismiss.Click += (_, _) => ClearServiceSuggestion();
        Routes.ServiceAction.SelectionChanged += (_, _) => UpdateServiceAction();
        UpdateServiceAction();
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
            return new ServiceRouteRow(route.Id, ClientServiceRoutes.Title(route),
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
        ClearServiceSuggestion();
        var preset = Routes.ServicePresetPicker.SelectedItem as ServicePreset;
        var saved = runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == preset?.Id);
        serviceFiles = saved?.Files ?? [];
        Routes.ServiceDraftTargets.Text = string.Join(", ", preset?.Domains ?? []);
        Routes.ServiceAction.SelectedIndex = 0;
        UpdateServiceAction();
        LoadServiceServers(saved?.ProfileId, preserveSelection: false);
        ShowServiceFiles();
    }

    private void LoadCustomServiceDraft(string id, string[] domains, ProgramRule[] files)
    {
        ClearServiceSuggestion();
        var saved = runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == id);
        domains = saved?.Domains ?? domains;
        files = saved?.Files ?? files;
        Routes.ServicePresetPicker.SelectedIndex = -1;
        customServiceDraft = new(id, "", domains, files);
        serviceFiles = files;
        Routes.ServiceDraftTargets.Text = string.Join(", ", domains);
        Routes.ServiceAction.SelectedIndex = 0;
        UpdateServiceAction();
        LoadServiceServers(saved?.ProfileId, preserveSelection: false);
        ShowServiceFiles();
    }

    private void UpdateServiceAction()
    {
        var pinned = Routes.ServiceAction.SelectedIndex == 0 || customServiceDraft is not null;
        Routes.ServiceServerPicker.Visibility = Routes.ServiceServerLabel.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        Routes.ServiceAction.IsEnabled = customServiceDraft is null;
    }

    private void LoadServiceServers(string? id = null, bool preserveSelection = true)
    {
        if (preserveSelection && Routes.ServiceServerPicker.SelectedItem is ServiceServer selected)
            id = ClientServerCatalog.Id(selected.Profile);
        else if (preserveSelection)
        {
            var routeId = customServiceDraft?.Id ?? (Routes.ServicePresetPicker.SelectedItem as ServicePreset)?.Id;
            id = runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == routeId)?.ProfileId;
        }
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

    private void ShowServiceFiles()
    {
        Routes.ServiceFileNames.Text = string.Join(", ", serviceFiles.Select(f => f.Executable));
        Routes.ServiceClearFiles.Visibility = serviceFiles.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveServiceRoute()
    {
        var preset = Routes.ServicePresetPicker.SelectedItem as ServicePreset;
        if (ruleSetDraft is not null && Routes.ServiceAction.SelectedIndex == 0)
        {
            MessageBox.Show(this, "Сначала сохраните новый набор.", "Маршрутизация");
            return;
        }
        if (preset is null && customServiceDraft is null)
        {
            MessageBox.Show(this, "Выберите шаблон.", "Маршрутизация");
            return;
        }
        if (preset is not null && Routes.ServiceAction.SelectedIndex is > 0 and var index)
        {
            SaveTemplateRules(preset, index switch { 2 => SiteAction.Direct, 3 => SiteAction.Block, _ => SiteAction.Tunnel });
            return;
        }
        if (Routes.ServiceServerPicker.SelectedItem is not ServiceServer server)
        {
            MessageBox.Show(this, "Выберите сервис и VPN-сервер.", "Маршрутизация");
            return;
        }
        try
        {
            var draft = runtime.ServiceRoutes.Draft(preset ?? new(customServiceDraft!.Id, "", customServiceDraft.Domains), server.Profile, serviceFiles);
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

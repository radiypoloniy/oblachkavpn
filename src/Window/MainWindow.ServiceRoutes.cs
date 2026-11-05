using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Oblachka;

public sealed record ServiceRouteRow(string Id, string Name, string Server, string Targets, string Status, bool Warning)
{
    public override string ToString() => Name;
}

public partial class MainWindow
{
    private ServiceRoute[] displayedServiceRoutes = [];
    private (string Text, bool Warning)[] displayedServiceStatus = [];
    private IReadOnlyList<Profile>? displayedServiceProfiles;
    private sealed record ServiceServer(string Name, Profile Profile)
    {
        public override string ToString() => Name;
    }
    private ProgramRule[] serviceFiles = [];
    private ServiceRoute? customServiceDraft;

    private void WireServiceRoutes()
    {
        Home.ServiceSummary.AllRoutes.Click += (_, _) => ShowRulesSection();
        Home.ServiceSummary.QuickRoutes.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, args) =>
        {
            if (args.OriginalSource is not Button { DataContext: ServiceRouteRow route }) return;
            ShowRulesSection();
            OpenSavedRoute(route);
        }));
        Routes.ServicePresetPicker.ItemsSource = ClientServiceRoutes.Presets;
        Routes.ServicePresetPicker.SelectionChanged += (_, _) =>
        {
            if (Routes.ServicePresetPicker.SelectedItem is not ServicePreset) return;
            customServiceDraft = null;
            LoadServiceDraft();
        };
        Routes.RuleList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, args) =>
        {
            if (args.OriginalSource is Button { Tag: "pinned", DataContext: ServiceRouteRow row }) OpenSavedRoute(row);
        }));
        Routes.ServiceServerPicker.DropDownOpened += (_, _) => LoadServiceServers();
        Routes.ServiceFiles.Click += (_, _) => PickServiceFiles();
        Routes.ServiceClearFiles.Click += (_, _) => { serviceFiles = []; ShowServiceFiles(); };
        Routes.ServiceSave.Click += (_, _) => SaveServiceRoute();
        Routes.ServiceCancel.Click += (_, _) => { CloseRouteEditor(); Routes.RuleDomain.Focus(); };
        Routes.IsVisibleChanged += (_, _) => { if (!Routes.IsVisible) CloseRouteEditor(); };
        Routes.ServiceDelete.Click += (_, _) =>
        {
            var id = customServiceDraft?.Id ?? (Routes.ServicePresetPicker.SelectedItem as ServicePreset)?.Id;
            if (id is null) return;
            runtime.ServiceRoutes.Remove(id);
            FinishRouteEdit();
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

    private void RefreshServiceRoutes()
    {
        var routes = runtime.TunnelPolicy.ServiceRoutes;
        var pending = runtime.ServiceRoutes.HasPendingChanges;
        Routes.ServiceApply.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        Routes.ServiceApplyPanel.Visibility = ruleSetDraft is null && (pending || routes.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        Routes.ServiceApplyNote.Text = pending ? "Привязки сохранены. Для применения переподключите VPN."
            : !runtime.Preferences.SystemTunnel ? "Маршруты действуют в режиме «Весь компьютер»."
            : runtime.Tunnel is null ? "Маршруты применятся при следующем подключении." : "";
        if (Routes.ServiceApplyNote.Text.Length == 0) Routes.ServiceApplyPanel.Visibility = Visibility.Collapsed;
        var status = routes.Select(runtime.ServiceRoutes.Status).ToArray();
        if (ReferenceEquals(displayedServiceProfiles, importedProfiles) && displayedServiceRoutes.SequenceEqual(routes) &&
            displayedServiceStatus.SequenceEqual(status)) return;
        displayedServiceProfiles = importedProfiles;
        displayedServiceRoutes = routes.ToArray();
        displayedServiceStatus = status;
        var rows = routes.Select((route, index) =>
        {
            var server = ClientServerCatalog.Find(runtime, route.ProfileId);
            return new ServiceRouteRow(route.Id, ClientServiceRoutes.Title(route),
                server is null ? "Сервер отсутствует" : ClientServerCatalog.Title(server),
                string.Join(", ", route.Domains.Concat(route.Files.Select(file => file.Executable))), status[index].Text, status[index].Warning);
        }).ToArray();
        Routes.SetPinnedRules(rows);
        Home.ServiceSummary.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Home.ServiceSummary.RouteCount.Text = rows.Length.ToString();
        Home.ServiceSummary.QuickRoutes.ItemsSource = rows.OrderByDescending(row => row.Warning).Take(2).ToArray();
    }

    private bool ConfirmPinnedConflicts(IEnumerable<SiteRule> sites, IEnumerable<ProgramRule> programs, string? exceptId = null)
    {
        var conflicts = runtime.ServiceRoutes.ConflictingRoutes(sites, programs, exceptId);
        return conflicts.Length == 0 || AppDialog.Confirm(this, "Пересечение со строгими маршрутами",
            "Для совпавшего трафика продолжат действовать привязки " + string.Join(", ", conflicts) + ".",
            "Сохранить с приоритетом привязок");
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
        var target = preset ?? new(customServiceDraft!.Id, Routes.EditorTitle.Text, customServiceDraft.Domains);
        if (preset is not null && ruleSetDraft is null &&
            runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(route => route.Id == preset.Id) is { } saved)
            target = target with { Domains = saved.Domains };
        if (Routes.ServiceAction.SelectedIndex is > 0 and var index)
        {
            if (runtime.TunnelPolicy.ServiceRoutes.Any(r => r.Id == target.Id) && !AppDialog.Confirm(this, "Заменить строгую привязку?",
                "После применения трафик пойдёт по выбранному обычному правилу.", "Заменить", danger: true)) return;
            if (!SaveTargetRules(target.Name, target.Domains, index switch { 2 => SiteAction.Direct, 3 => SiteAction.Block, _ => SiteAction.Tunnel }, target.Id)) return;
            if (ruleSetDraft is null) runtime.ServiceRoutes.Remove(target.Id);
            FinishRouteEdit();
            return;
        }
        if (Routes.ServiceServerPicker.SelectedItem is not ServiceServer server)
        {
            MessageBox.Show(this, "Выберите VPN-сервер.", "Маршрутизация");
            return;
        }
        try
        {
            var draft = runtime.ServiceRoutes.Draft(target, server.Profile, serviceFiles);
            var conflicts = runtime.ServiceRoutes.Conflicts(draft);
            if (conflicts.Length > 0 && MessageBox.Show(this,
                "Привязка получит приоритет над этими правилами для совпавшего трафика:\n\n" +
                string.Join("\n", conflicts.Take(6)) + (conflicts.Length > 6 ? $"\nЕщё правил: {conflicts.Length - 6}" : "") +
                "\n\nСохранить привязку?", "Пересечение правил", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            runtime.ServiceRoutes.Save(draft);
            FinishRouteEdit();
        }
        catch (UserError error) { MessageBox.Show(this, error.Message, "Маршрутизация"); }
    }

    private async Task ApplyServiceRoutesAsync()
    {
        if (!runtime.ServiceRoutes.HasPendingChanges || runtime.Profiles.Running is not { } running) return;
        if (MessageBox.Show(this, "Соединения прервутся. Пока туннель перестраивается, сеть будет заблокирована. Продолжить?",
            "Переподключение VPN", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { await runtime.Connections.RestartAsync(running, rebuildTunnel: true); }
        catch (UserError error) { MessageBox.Show(this, error.Message, "Маршрутизация"); }
        if (!closed) RefreshServiceRoutes();
    }
}

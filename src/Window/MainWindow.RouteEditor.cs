using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private void OpenRouteTemplates()
    {
        CloseRouteEditor();
        Routes.ServicePresetPicker.SelectedIndex = 0;
        Routes.ShowTargetEditor("Готовый шаблон", templates: true);
    }

    private void OpenSavedRoute(string id)
    {
        if (ruleSetDraft is not null)
        { DetailText.Text = "Завершите редактирование нового набора, чтобы изменить сохранённую привязку."; return; }
        if (runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == id) is not { } route) return;
        var title = ClientServiceRoutes.Title(route);
        CloseRouteEditor();
        if (ClientServiceRoutes.Presets.FirstOrDefault(p => p.Id == route.Id) is { } preset)
        {
            Routes.ServicePresetPicker.SelectedItem = preset;
            Routes.ShowTargetEditor(title, templates: false);
        }
        else
        {
            LoadCustomServiceDraft(route.Id, route.Domains, route.Files);
            Routes.ShowTargetEditor(title, templates: false);
        }
    }

    private void CloseRouteEditor()
    {
        Routes.CloseTargetEditor();
        Routes.ServicePresetPicker.SelectedIndex = -1;
        customServiceDraft = null;
        ClearServiceSuggestion();
    }

    private void FinishRouteEdit()
    {
        CloseRouteEditor();
        Routes.RuleDomain.Clear();
        RefreshServiceRoutes();
        Routes.RuleDomain.Focus();
    }

    private void LoadServiceDraft()
    {
        ClearServiceSuggestion();
        var preset = Routes.ServicePresetPicker.SelectedItem as ServicePreset;
        var saved = ruleSetDraft is null ? runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == preset?.Id) : null;
        serviceFiles = saved?.Files ?? [];
        ShowRouteDomains(saved?.Domains ?? preset?.Domains ?? []);
        SelectRouteAction(canPin: true, saved is not null ? null : SiteAction.Tunnel);
        LoadServiceServers(saved?.ProfileId, preserveSelection: false);
        ShowServiceFiles();
    }

    private void LoadCustomServiceDraft(string id, string[] domains, ProgramRule[] files, SiteAction? action = null)
    {
        ClearServiceSuggestion();
        var saved = ruleSetDraft is null ? runtime.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == id) : null;
        domains = saved?.Domains ?? domains;
        files = saved?.Files ?? files;
        Routes.ServicePresetPicker.SelectedIndex = -1;
        customServiceDraft = new(id, "", domains, files);
        serviceFiles = files;
        ShowRouteDomains(domains);
        SelectRouteAction(domains.All(domain => SiteTarget.Kind(domain) == SiteTargetKind.Domain), saved is not null ? null : action);
        LoadServiceServers(saved?.ProfileId, preserveSelection: false);
        ShowServiceFiles();
    }

    private void SelectRouteAction(bool canPin, SiteAction? action)
    {
        canPin &= ruleSetDraft is null;
        Routes.PinnedAction.Visibility = canPin ? Visibility.Visible : Visibility.Collapsed;
        Routes.ServiceAction.SelectedIndex = action switch { SiteAction.Direct => 2, SiteAction.Block => 3, null when canPin => 0, _ => 1 };
        UpdateServiceAction();
    }

    private void ShowRouteDomains(string[] domains)
    {
        Routes.ServiceDraftTargets.Text = string.Join(", ", domains);
        Routes.ServiceDraftTargets.Visibility = domains.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateServiceAction()
    {
        var pinned = Routes.ServiceAction.SelectedIndex == 0;
        Routes.ServiceServerField.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        var id = customServiceDraft?.Id ?? (Routes.ServicePresetPicker.SelectedItem as ServicePreset)?.Id;
        Routes.ServiceDelete.Visibility = ruleSetDraft is null && runtime.TunnelPolicy.ServiceRoutes.Any(route => route.Id == id)
            ? Visibility.Visible : Visibility.Collapsed;
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
        Routes.ServiceFileNames.Visibility = serviceFiles.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Routes.ServiceClearFiles.Visibility = serviceFiles.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

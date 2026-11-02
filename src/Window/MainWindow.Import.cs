using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace Oblachka;

public partial class MainWindow
{
    // Вставленная ссылка и открытый файл — всегда сторонний источник: ключ сервиса
    // вводится на первом экране и проходит через ServiceKey.
    private void Import(string input)
    {
        try
        {
            runtime.Imports.ImportText(input);
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }
    // Прежняя форма для проверок: узлы сервиса — в его группу, остальные — во «Вручную».
    private void ApplyImport(ImportResult result, bool preserveSelection = false) =>
        ApplyGroupImport(result.Profiles.Count > 0 && result.Profiles[0].Source == ProfileSource.Service
            ? (ServiceGroup ?? new(ProfileGroup.ServiceId, ServiceBrand.Name, ProfileSource.Service, Array.Empty<string>())) with { Metadata = result.Metadata }
            : ProfileGroup.Manual, result, preserveSelection);

    // Подписка заменяет свою группу целиком; «Вручную» пополняется. Остальные группы не трогаются.
    private void ApplyGroupImport(ProfileGroup group, ImportResult result, bool preserveSelection = false)
    {
        try { runtime.Imports.Apply(group, result, preserveSelection); }
        catch (UserError error) { DetailText.Text = error.Message; }
    }
    private void ImportApplied(ClientImportChange change)
    {
        if (closed) return;
        ApplyProfiles(true);
        if (change.Result.Metadata?.Routing is not null) RefreshRouting();
        if (change.NewSubscription && Servers.GroupTabs.Tag is string[] ids && Array.IndexOf(ids, change.Group.Id) is var tab && tab >= 0)
            Servers.GroupTabs.SelectedIndex = tab + 1;
        UpdateControls();
    }
    private void ImportsChanged()
    {
        if (closed) return;
        DetailText.Text = runtime.Imports.Message;
        UpdateControls();
    }

    private void ApplyProfiles(bool preserveSelection)
    {
        // Скрытые серверы подписок в список не попадают (MainWindow.ServerRemoval).
        var combined = groups.SelectMany(g => ProfilesOf(g.Id)).Where(p => !HiddenServer(p)).ToArray();
        RefreshGroups();
        Dispatcher.BeginInvoke(() =>
        {
            if (Settings.IsVisible && Settings.CurrentPage == Views.SettingsPanel.AutoPoolPage) RenderAutoPool();
            else Settings.AutoPoolList.ItemsSource = null;
        });
        if (combined.Length == 0)
        {
            // Убрали последнюю подписку: список пуст, главный экран снова предлагает ключ.
            ProfilePicker.ItemsSource = importedProfiles = Array.Empty<Profile>();
            machineProfiles = null; machineIndex.Clear();
            profile = null; ShowProfile(); FilterProfiles(); UpdateSubscriptionSummary(); Navigate(currentPage);
            TrimMeasurements();
            return;
        }
        var selection = ProfileSelection.Find(combined, preserveSelection ? profile : null);
        ProfilePicker.ItemsSource = combined;
        importedProfiles = combined;
        machineProfiles = null; machineIndex.Clear();
        Servers.SearchBox.Clear();
        UpdateSubscriptionSummary();
        ProfilePicker.SelectedIndex = selection;
        profile = combined[selection];
        TrimMeasurements();
        routeAttempts = Array.Empty<RouteAttempt>();
        ShowProfile();
        FilterProfiles();
        QueuePing();
    }
    private void ProfileSelectionChanged(object? sender, EventArgs args)
    {
        if (ProfilePicker.SelectedItem is not Profile selected) return;
        profile = selected; ShowProfile(); UpdateControls(); QueueStateSave();
        QueueServerSwitch();
    }
    private void ShowProfile()
    {
        ProfileNameText.Text = profile?.DisplayName ?? "Профиль ещё не добавлен";
        TransportText.Text = profile is null ? "VLESS-ссылка или клиентский Xray / sing-box JSON" : profile.Core + " · " + profile.Transport;
    }
    private async void SubscriptionClick(object sender, RoutedEventArgs args)
    {
        var url = AppDialog.Ask(this, "Адреса подписки", "Основной и резервные HTTPS-адреса: каждый с новой строки, до пяти.", "https://…", "Загрузить",
            string.Join("\n", CurrentGroup?.Sources ?? Array.Empty<string>()), "Полные ссылки для каждого адреса. Они хранятся в зашифрованном виде.", multiline: true);
        if (url is null) return;
        try { await LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(url), 0); }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
    }
    // Обновляем все подписки: сервиса и сторонние.
    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        if (operation is null) await RefreshAllAsync();
    }
    private Task LoadSubscriptionAsync(IReadOnlyList<string> sources, int preferred,
        Func<IReadOnlyList<string>, int, CancellationToken, Task<MirrorResult>>? loader = null) => runtime.Imports.LoadAsync(sources, preferred, loader);
    private void ImportFileClick(object sender, RoutedEventArgs args)
    {
        Servers.AddPopup.IsOpen = false;
        var dialog = new OpenFileDialog { Filter = "Профиль (*.json;*.txt;*.conf)|*.json;*.txt;*.conf|Все файлы|*.*", Title = "Открыть клиентский профиль" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > Profiles.MaxBytes) { DetailText.Text = "Файл больше 1 МБ. Выберите профиль одного подключения."; return; }
            Import(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось прочитать файл. Проверьте доступ к нему."; }
    }
    // Поле «Добавить сервер»: ссылка https:// — подписка своей группой, остальное — во «Вручную».
    private async void AddKeyClick(object sender, RoutedEventArgs args)
    {
        var text = Servers.Add.KeyInput.Text.Trim();
        if (text.Length == 0 || operation is not null) return;
        if (text.Length > Profiles.MaxBytes) { DetailText.Text = "Текст больше 1 МБ. Вставьте профиль одного подключения."; return; }
        Servers.AddPopup.IsOpen = false;
        Servers.Add.KeyInput.Clear();
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !text.Contains('\n'))
        {
            try { await LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(text), 0); }
            catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        }
        else Import(text);
    }
}

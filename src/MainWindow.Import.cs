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
            var result = ShareLinks.IsLink(input.Trim()) && !input.Trim().Contains('\n')
                ? new ImportResult(new[] { Profiles.Parse(input) }, 0, 0) : Subscriptions.Parse(input);
            // Вставленное добавляется во «Вручную» к прежним; подписки не трогаются.
            ApplyGroupImport(ProfileGroup.Manual, result.WithSource(ProfileSource.External));
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
        try
        {
            SetGroup(group, group.IsManual ? ProfileGroups.Append(ProfilesOf(ProfileGroup.ManualId), result.Profiles) : result.Profiles);
        }
        catch (UserError ex) { DetailText.Text = ex.Message; return; }
        if (result.Metadata?.Routing is { } routing) ReceiveRouting(routing);
        ApplyProfiles(preserveSelection);
        // Добавленное вручную сразу выбрано: его и собирались подключать.
        if (group.IsManual && !preserveSelection && result.Profiles.Count > 0 &&
            importedProfiles.FirstOrDefault(p => p.Group == ProfileGroup.ManualId && p.Name == result.Profiles[0].Name &&
                Profiles.Fingerprint(p) == Profiles.Fingerprint(result.Profiles[0])) is { } added)
            SelectProfile(added);
        DetailText.Text = $"Добавлено подключений: {result.Profiles.Count}.";
        if (result.Unsupported + result.Invalid > 0) DetailText.Text += $" Пропущено: неподдерживаемых {result.Unsupported}, некорректных {result.Invalid}.";
        if (result.Reasons.Count > 0) DetailText.Text += " Причины: " + string.Join(" | ", result.Reasons);
        UpdateControls();
        QueueStateSave();
    }

    private void ApplyProfiles(bool preserveSelection)
    {
        var combined = groups.SelectMany(g => ProfilesOf(g.Id)).ToArray();
        RefreshGroups();
        if (combined.Length == 0)
        {
            // Убрали последнюю подписку: список пуст, главный экран снова предлагает ключ.
            ProfilePicker.ItemsSource = importedProfiles = Array.Empty<Profile>();
            profile = null; ShowProfile(); FilterProfiles(); UpdateSubscriptionSummary(); Navigate(currentPage);
            return;
        }
        var selection = ProfileSelection.Find(combined, preserveSelection ? profile : null);
        ProfilePicker.ItemsSource = combined;
        importedProfiles = combined;
        Servers.SearchBox.Clear();
        UpdateSubscriptionSummary();
        ProfilePicker.SelectedIndex = selection;
        profile = combined[selection];
        routeAttempts = Array.Empty<RouteAttempt>();
        ShowProfile();
        FilterProfiles();
        QueuePing();
    }
    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs args)
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
        var dialog = new Window { Owner = this, Title = "Адреса подписки", Width = 680, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, FontFamily = FontFamily };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Основной и резервные HTTPS-адреса: каждый с новой строки, до пяти.", Foreground = Foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var input = new TextBox { MaxLength = 20 * 1024, AcceptsReturn = true, Height = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = string.Join("\n", subscriptionSources) }; panel.Children.Add(input);
        panel.Children.Add(new TextBlock { Text = "Полные ссылки для каждого адреса. При включённом запоминании они сохраняются в зашифрованном виде.", Foreground = Foreground, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) });
        var button = new Button { Content = "Загрузить", HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(button);
        string? url = null;
        button.Click += (_, _) => { url = input.Text; input.Clear(); dialog.DialogResult = true; };
        dialog.Closed += (_, _) => input.Clear();
        dialog.Content = panel; StyleDialog(dialog);
        if (dialog.ShowDialog() != true || url is null) return;
        try { await LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(url), 0); }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        finally { url = null; }
    }
    // Обновляем подписку сервиса; если её нет, обновляем стороннюю.
    // Обновляем все подписки: сервиса и сторонние.
    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        if (operation is null) await RefreshAllAsync();
    }
    private async Task LoadSubscriptionAsync(IReadOnlyList<string> sources, int preferred,
        Func<IReadOnlyList<string>, int, CancellationToken, Task<MirrorResult>>? loader = null)
    {
        if (operation is not null) return;
        manualStop = false;
        operation = new CancellationTokenSource(); engineOperation = false; UpdateControls();
        DetailText.Text = "Загружаем подписку; при отказе проверим резервные адреса…";
        try
        {
            var result = await (loader ?? SubscriptionMirrors.FetchAsync)(sources, preferred, operation.Token);
            subscriptionSchedule.Complete(DateTimeOffset.UtcNow, true, result.Import.Metadata?.UpdateInterval);
            operation.Token.ThrowIfCancellationRequested();
            if (closed) return;
            var kind = ServiceKey.SourceOf(sources);
            var id = ProfileGroup.IdFor(sources, kind);
            var previous = groups.FirstOrDefault(g => g.Id == id);
            var metadata = result.Import.Metadata ?? previous?.Metadata;
            var group = new ProfileGroup(id, ProfileGroup.NameFor(sources, metadata, kind), kind, sources.ToArray())
                { PreferredSource = result.SourceIndex, Metadata = metadata, Updated = DateTimeOffset.UtcNow };
            var imported = result.Import.WithSource(kind);
            if (engine.State is ConnectionState.Running or ConnectionState.Verified)
            {
                pendingImport = (group, imported);
                DetailText.Text = $"Получено подключений: {result.Import.Profiles.Count}; адрес №{result.SourceIndex + 1}. Текущий туннель сохранён. Новый список применится после остановки.";
            }
            else { ApplyGroupImport(group, imported, true); DetailText.Text += $" Загрузка: адрес №{result.SourceIndex + 1}, попыток {result.Attempts}."; }
        }
        catch (OperationCanceledException) { subscriptionSchedule.Complete(DateTimeOffset.UtcNow, false); if (!closed) DetailText.Text = "Загрузка отменена. Предыдущий список сохранён."; }
        catch (SubscriptionAccessDenied ex) { subscriptionSchedule.AccessDenied(); if (!closed) DetailText.Text = ex.Message; }
        catch (UserError ex) { subscriptionSchedule.Complete(DateTimeOffset.UtcNow, false); if (!closed) DetailText.Text = ex.Message; }
        finally { if (manualStop) engine.Stop(); operation.Dispose(); operation = null; if (!closed) UpdateControls(); }
    }
    private void ImportFileClick(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Filter = "Профиль (*.json;*.txt)|*.json;*.txt|Все файлы|*.*", Title = "Открыть клиентский профиль" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > Profiles.MaxBytes) { DetailText.Text = "Файл больше 1 МБ. Выберите профиль одного подключения."; return; }
            Import(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось прочитать файл. Проверьте доступ к нему."; }
    }
    private void PasteClick(object sender, RoutedEventArgs args)
    {
        var dialog = new Window { Owner = this, Title = "Добавить профиль", Width = 620, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, FontFamily = FontFamily };
        var panel = new DockPanel { Margin = new Thickness(24) };
        var caption = new TextBlock { Text = "Вставьте ссылку vless://, trojan://, hy2:// или клиентский Xray / sing-box JSON.", Margin = new Thickness(0, 0, 0, 12), Foreground = Foreground };
        DockPanel.SetDock(caption, Dock.Top); panel.Children.Add(caption);
        var button = new Button { Content = "Добавить профиль", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = Profiles.MaxBytes };
        panel.Children.Add(input); dialog.Content = panel; StyleDialog(dialog);
        button.Click += (_, _) => { Import(input.Text); input.Clear(); dialog.Close(); };
        dialog.Closed += (_, _) => input.Clear();
        dialog.ShowDialog();
    }
}

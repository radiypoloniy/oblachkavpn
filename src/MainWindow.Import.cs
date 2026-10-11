using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace RoutePilot;

public partial class MainWindow
{
    private void Import(string input)
    {
        try
        {
            var result = input.Trim().StartsWith("vless://", StringComparison.OrdinalIgnoreCase) && !input.Trim().Contains('\n')
                ? new ImportResult(new[] { Profiles.Parse(input) }, 0, 0) : Subscriptions.Parse(input);
            subscriptionSources = Array.Empty<string>(); pendingImport = null;
            ApplyImport(result);
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }
    private void ApplyImport(ImportResult result, bool preserveSelection = false)
    {
        var selection = ProfileSelection.Find(result.Profiles, preserveSelection ? profile : null);
        subscriptionMetadata = result.Metadata;
        ProfilePicker.ItemsSource = result.Profiles;
        importedProfiles = result.Profiles;
        Servers.SearchBox.Clear();
        ProfilePicker.Visibility = Visibility.Visible;
        Servers.SubscriptionSummary.Text = $"Подключений: {result.Profiles.Count}";
        UpdateSubscriptionSummary();
        ProfilePicker.SelectedIndex = selection;
        profile = result.Profiles[selection];
        routeAttempts = Array.Empty<RouteAttempt>();
        ShowProfile();
        DetailText.Text = $"Добавлено подключений: {result.Profiles.Count}.";
        if (result.Unsupported + result.Invalid > 0) DetailText.Text += $" Пропущено: неподдерживаемых {result.Unsupported}, некорректных {result.Invalid}.";
        UpdateControls();
        QueueStateSave();
    }
    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProfilePicker.SelectedItem is not Profile selected) return;
        profile = selected; ShowProfile(); UpdateControls(); QueueStateSave();
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
        dialog.Content = panel;
        if (dialog.ShowDialog() != true || url is null) return;
        try { await LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(url), 0); }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        finally { url = null; }
    }
    private async void RefreshClick(object sender, RoutedEventArgs args)
    { if (subscriptionSources.Count > 0 && operation is null) await LoadSubscriptionAsync(subscriptionSources, preferredSource); }
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
            subscriptionSchedule.Complete(DateTimeOffset.UtcNow, true);
            operation.Token.ThrowIfCancellationRequested();
            if (closed) return;
            subscriptionSources = sources.ToArray(); preferredSource = result.SourceIndex;
            if (engine.State is ConnectionState.Running or ConnectionState.Verified)
            {
                pendingImport = result.Import;
                DetailText.Text = $"Получено подключений: {result.Import.Profiles.Count}; адрес №{result.SourceIndex + 1}. Текущий туннель сохранён. Новый список применится после остановки.";
            }
            else { ApplyImport(result.Import, true); DetailText.Text += $" Загрузка: адрес №{result.SourceIndex + 1}, попыток {result.Attempts}."; }
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
            if (new FileInfo(dialog.FileName).Length > Profiles.MaxBytes) { DetailText.Text = "Файл больше 1 МБ. Выбери профиль одного подключения."; return; }
            Import(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось прочитать файл. Проверь доступ к нему."; }
    }
    private void PasteClick(object sender, RoutedEventArgs args)
    {
        var dialog = new Window { Owner = this, Title = "Добавить профиль", Width = 620, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, FontFamily = FontFamily };
        var panel = new DockPanel { Margin = new Thickness(24) };
        var caption = new TextBlock { Text = "Вставь VLESS-ссылку или клиентский Xray / sing-box JSON.", Margin = new Thickness(0, 0, 0, 12), Foreground = Foreground };
        DockPanel.SetDock(caption, Dock.Top); panel.Children.Add(caption);
        var button = new Button { Content = "Добавить профиль", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = Profiles.MaxBytes };
        panel.Children.Add(input); dialog.Content = panel;
        button.Click += (_, _) => { Import(input.Text); input.Clear(); dialog.Close(); };
        dialog.Closed += (_, _) => input.Clear();
        dialog.ShowDialog();
    }
}

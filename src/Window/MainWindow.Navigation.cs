using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Oblachka;

public partial class MainWindow
{
    private string currentPage = "Connection";
    private bool viewsReady;
    private bool exitRequested;
    // Общий список для интерфейса: сначала узлы сервиса, затем сторонние.
    private IReadOnlyList<Profile> importedProfiles = Array.Empty<Profile>();

    // Экраны нового дизайна: главный; «Все серверы» поверх него; настройки с вкладками
    // («Правила» и «Помощь» — их вкладки). Без подключений — экран ключа.
    private void Navigate(string page)
    {
        currentPage = page;
        var welcome = profile is null && page == "Connection";
        Home.Visibility = !welcome && page is "Connection" or "Servers" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Servers")
        {
            if (Servers.Visibility != Visibility.Visible) { FilterProfiles(); Servers.Open(); }
            if (DateTimeOffset.UtcNow - lastPing > TimeSpan.FromMinutes(5)) QueuePing();
        }
        else Servers.Visibility = Visibility.Collapsed;
        if (page is "Routes" or "Diagnostics") OpenSettings(page == "Routes" ? 1 : 3);
        else { Settings.Visibility = Visibility.Collapsed; Picker.Visibility = Visibility.Collapsed; }
        Onboarding.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
        Onboarding.ContinueButton.IsDefault = welcome;
    }

    // Подписки других клиентов ищутся один раз за запуск и только в настоящем окружении:
    // проверки интерфейса не должны видеть файлы того, кто их запускает.
    private bool foreignSearched;
    private async void FindForeignClients()
    {
        if (foreignSearched || !stateRestored || stateStore is null || isolatedPersistence) return;
        foreignSearched = true;
        var found = await Task.Run(() => ForeignClients.Find());
        if (closed || found.Count == 0) return;
        Onboarding.ForeignList.ItemsSource = found;
        Onboarding.ForeignPanel.Visibility = Visibility.Visible;
    }

    private async void ForeignClick(object sender, RoutedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is not ForeignSubscription found || operation is not null) return;
        if (found.IsLocal) ImportForeignProfiles(found);
        else await ContinueWithAsync(found.Url, clearField: false);
    }

    // Отдельные подключения другого клиента — во «Вручную», рядом с подписками.
    private void ImportForeignProfiles(ForeignSubscription found)
    {
        ApplyGroupImport(ProfileGroup.Manual, new ImportResult(found.Profiles, 0, 0).WithSource(ProfileSource.External));
        Navigate("Connection");
    }

    private void SettingsClick(object sender, RoutedEventArgs args) => OpenSettings(null);

    private void OpenSettings(int? tab)
    {
        ShowModeMenu(false);
        if (tab is int index) Settings.ShowTab(index);
        if (Settings.Visibility != Visibility.Visible)
        {
            Settings.Visibility = Visibility.Visible;
            Settings.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }
        RefreshSettings();
        Settings.CloseButton.Focus();
    }

    private void CloseSettings()
    {
        Settings.Visibility = Visibility.Collapsed;
        Picker.Visibility = Visibility.Collapsed;
        Doctor.Visibility = Visibility.Collapsed;
        if (currentPage is "Routes" or "Diagnostics") currentPage = "Connection";
        Home.SettingsButton.Focus();
    }

    private void ExitApplication() { exitRequested = true; Close(); }

    // Esc закрывает верхний слой: меню режима, настройки, «Все серверы».
    private void WindowKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        if (ModeMenuLayer.Visibility == Visibility.Visible) ShowModeMenu(false);
        else if (Picker.Visibility == Visibility.Visible) CloseAppPicker();
        else if (Doctor.Visibility == Visibility.Visible) Doctor.Visibility = Visibility.Collapsed;
        else if (Settings.Visibility == Visibility.Visible) CloseSettings();
        else if (Servers.Visibility == Visibility.Visible) Navigate("Connection");
        else return;
        args.Handled = true;
    }

    // Поиск и порядок панели «Все серверы». Выбранный сервер не теряется, даже если поиск его
    // скрыл: модель выбора остаётся прежней, меняется только показанный список.
    private void FilterProfiles()
    {
        if (!viewsReady) return;
        var query = Servers.SearchBox.Text.Trim();
        var selected = profile;
        RefreshServerLabels();
        var tab = Servers.GroupTabs.SelectedIndex > 0 && Servers.GroupTabs.SelectedIndex <= groups.Count ? groups[Servers.GroupTabs.SelectedIndex - 1].Id : null;
        var visible = importedProfiles.Where(p => (tab is null || p.Group == tab) &&
            (p.Country.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
        visible = Servers.SortOrder.SelectedIndex switch
        {
            0 => visible.OrderBy(MeasurementOrder),
            1 => visible.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => visible
        };
        ProfilePicker.ItemsSource = visible.ToArray();
        ProfilePicker.SelectedItem = selected;
        RenderSheet();
    }

    // Первый экран: одно поле на всё. Ключ и ссылка своего сервиса — подписка сервиса;
    // ссылка другого сервиса — сторонняя подписка; ссылки профилей и JSON — импорт.
    // После загрузки сразу подключаемся: «Подключиться» и значит подключиться.
    private async void ContinueSubscription(object sender, RoutedEventArgs args) =>
        await ContinueWithAsync(Onboarding.KeyInput.Text, clearField: true);

    private async Task ContinueWithAsync(string text, bool clearField)
    {
        try
        {
            // Оставленная аварийно завершённым окном сессия держит сеть закрытой: подписку
            // тогда не загрузить. Снимаем её до загрузки, как и обычное подключение.
            if (NetworkHelperConnection.ServiceRunning && SessionLeft(NetworkService.JournalPath))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await RecoverServiceSessionAsync(timeout.Token); }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
            }
            switch (ServiceKey.Classify(text))
            {
                case ServiceKey.InputKind.Profiles: Import(text); break;
                case ServiceKey.InputKind.ExternalUrl: await LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(text), 0); break;
                default: await LoadSubscriptionAsync(new[] { ServiceKey.ToSubscriptionUrl(text) }, 0); break;
            }
        }
        catch (UserError ex) { DetailText.Text = ex.Message; return; }
        if (profile is null) return;
        if (clearField) Onboarding.KeyInput.Clear();
        Navigate("Connection");
        if (ConnectButton.IsEnabled) ConnectClick(this, new RoutedEventArgs());
    }
}

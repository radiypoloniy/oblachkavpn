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
    // Наборы живут раздельно, поэтому загрузка одного не стирает другой.
    private IReadOnlyList<Profile> serviceProfiles = Array.Empty<Profile>();
    private IReadOnlyList<Profile> externalProfiles = Array.Empty<Profile>();

    // Экраны нового дизайна: главный; «Все серверы» поверх него; настройки с вкладками
    // («Правила» и «Помощь» — их вкладки). Без подключений — экран ключа.
    private void Navigate(string page)
    {
        currentPage = page;
        var welcome = profile is null && page == "Connection";
        Home.Visibility = !welcome && page is "Connection" or "Servers" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Servers") { if (Servers.Visibility != Visibility.Visible) { FilterProfiles(); Servers.Open(); } }
        else Servers.Visibility = Visibility.Collapsed;
        if (page is "Routes" or "Diagnostics") OpenSettings(page == "Routes" ? 1 : 3);
        else Settings.Visibility = Visibility.Collapsed;
        Onboarding.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
        Onboarding.ContinueButton.IsDefault = welcome;
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
        if (currentPage is "Routes" or "Diagnostics") currentPage = "Connection";
        Home.SettingsButton.Focus();
    }

    private void ExitApplication() { exitRequested = true; Close(); }

    // Esc закрывает верхний слой: меню режима, настройки, «Все серверы».
    private void WindowKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        if (ModeMenuLayer.Visibility == Visibility.Visible) ShowModeMenu(false);
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
        var visible = importedProfiles.Where(p => p.Country.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
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

    // Первый экран принимает ключ сервиса. Подписка другого сервиса идёт отдельным путём,
    // через «Вставить ссылку или JSON», и там же показывается соответствующая подсказка.
    private async void ContinueSubscription(object sender, RoutedEventArgs args)
    {
        try
        {
            await LoadSubscriptionAsync(new[] { ServiceKey.ToSubscriptionUrl(Onboarding.KeyInput.Text) }, 0);
            if (profile is not null) Onboarding.KeyInput.Clear();
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }
}

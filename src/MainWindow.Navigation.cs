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

    private void Navigate(string page)
    {
        currentPage = page;
        var welcome = profile is null && page == "Connection";
        // Главный экран — всё, что не временная страница. Без подключений — экран ключа.
        Home.Visibility = !welcome && page == "Connection" ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Visibility = page is "Servers" or "Routes" or "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "Servers" => "Все серверы", "Routes" => "Правила", "Diagnostics" => "Помощь", _ => "" };
        Servers.Visibility = page == "Servers" ? Visibility.Visible : Visibility.Collapsed;
        Routes.Visibility = page == "Routes" ? Visibility.Visible : Visibility.Collapsed;
        Diagnostics.Visibility = page == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        Onboarding.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
        Onboarding.ContinueButton.IsDefault = welcome;
    }

    private void SettingsClick(object sender, RoutedEventArgs args)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
        Settings.CloseButton.Focus();
    }

    private void CloseSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        Home.SettingsButton.Focus();
    }

    private void OverlayMouseDown(object sender, MouseButtonEventArgs args)
    { if (ReferenceEquals(args.OriginalSource, SettingsOverlay)) CloseSettings(); }

    private void ExitApplication() { exitRequested = true; Close(); }

    private void WindowKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && SettingsOverlay.Visibility == Visibility.Visible)
        { CloseSettings(); args.Handled = true; }
    }

    private void FilterProfiles()
    {
        if (!viewsReady || operation is not null || engine.State != ConnectionState.Idle) return;
        var query = Servers.SearchBox.Text.Trim();
        var selected = profile;
        RefreshServerLabels();
        var visible = importedProfiles.Where(p => (p.Country.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (Servers.FavoritesOnly.IsChecked != true || favorites.Contains(FavoriteKey(p))));
        visible = Servers.SortOrder.SelectedIndex switch
        {
            1 => visible.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            2 => visible.OrderBy(p => p.Country.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            3 => visible.OrderBy(MeasurementOrder),
            _ => visible
        };
        ProfilePicker.ItemsSource = visible.ToArray();
        ProfilePicker.SelectedItem = selected;
        Servers.SubscriptionSummary.Text = $"Подключений: {importedProfiles.Count}, показано: {ProfilePicker.Items.Count}";
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

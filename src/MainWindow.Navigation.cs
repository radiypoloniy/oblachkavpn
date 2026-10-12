using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RoutePilot;

public partial class MainWindow
{
    private string currentPage = "Connection";
    private bool viewsReady;
    private bool exitRequested;
    private IReadOnlyList<Profile> importedProfiles = Array.Empty<Profile>();

    private void NavigationChecked(object sender, RoutedEventArgs args)
    {
        if (viewsReady && sender is RadioButton button && button.Tag is string page) Navigate(page);
    }

    private void Navigate(string page)
    {
        currentPage = page;
        var welcome = profile is null && page == "Connection";
        Connection.Visibility = !welcome && page == "Connection" ? Visibility.Visible : Visibility.Collapsed;
        Servers.Visibility = page == "Servers" ? Visibility.Visible : Visibility.Collapsed;
        Routes.Visibility = page == "Routes" ? Visibility.Visible : Visibility.Collapsed;
        Diagnostics.Visibility = page == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        Onboarding.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
        Onboarding.ContinueButton.IsDefault = welcome;
        Sidebar.Visibility = welcome ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(welcome ? 0 : 210);
        var navigation = page switch { "Servers" => ServersNav, "Routes" => RoutesNav, "Diagnostics" => DiagnosticsNav, _ => ConnectionNav };
        if (navigation.IsChecked != true) navigation.IsChecked = true;
    }

    private void SettingsClick(object sender, RoutedEventArgs args)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
        Settings.MaxHeight = Math.Max(300, (ActualHeight > 0 ? ActualHeight : Height) - 70);
        Settings.CloseButton.Focus();
    }

    private void CloseSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        ConnectionNav.Focus();
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
        ProfilePicker.ItemsSource = importedProfiles.Where(p => p.Country.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
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

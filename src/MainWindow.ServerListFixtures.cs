using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckServerList(ICollection<string> checks)
    {
        var selected = profile!;
        Servers.FavoriteButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Servers.FavoritesOnly.IsChecked = true;
        FilterProfiles();
        if (ProfilePicker.Items.Count != 1 || profile != selected || CaptureState().Favorites.Length != 1)
            throw new InvalidOperationException("Favorite filter lost selection or was not captured for encrypted storage");
        Servers.SearchBox.Text = "no-synthetic-match";
        if (ProfilePicker.Items.Count != 0 || profile != selected)
            throw new InvalidOperationException("Empty favorite search changed connection");
        Servers.SearchBox.Clear();
        Servers.FavoritesOnly.IsChecked = false;
        Servers.SortOrder.SelectedIndex = 1;
        FilterProfiles();
        if (ProfilePicker.Items.Count != 2 || profile != selected || engine.ProcessId is not null)
            throw new InvalidOperationException("Sorting changed selected connection or started a core");
        checks.Add("Favorites and sorting preserve the selected connection and do not start networking");
        measurements[Profiles.Fingerprint(selected)] = new(Profiles.Fingerprint(selected), DateTimeOffset.UtcNow.AddMinutes(-21), true, 12);
        RefreshServerLabels();
        if (!selected.ListStatus.Contains("устарело") || !selected.ListStatus.Contains("★"))
            throw new InvalidOperationException("Stale measurement or favorite marker missing");
        measurements.Clear();
        Servers.FavoriteButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Servers.SortOrder.SelectedIndex = 0;
        FilterProfiles();
        checks.Add("Server rows mark stale measurements and favorites without changing profile names");
        var report = CreateSupportReport();
        if (!report.Contains("Revision") || !report.Contains("LocalProxy") || report.Contains("example.com"))
            throw new InvalidOperationException("Support report omitted build identity or included a server address");
        checks.Add("Support report includes build identity and actual mode without server addresses");
    }
}

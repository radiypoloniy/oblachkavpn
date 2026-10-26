using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckServerList(ICollection<string> checks)
    {
        var selected = profile!;
        Navigate("Servers");
        ToggleFavorite(Row(selected));
        var sheet = Servers.SheetList.Items.Cast<object>().ToArray();
        var favoriteHeader = Array.FindIndex(sheet, i => i is SheetHeader { Title: "Избранное" });
        if (favoriteHeader < 0 || sheet[favoriteHeader + 1] is not ServerRow { Favorite: true } starred || starred.Profile != selected ||
            profile != selected || CaptureState().Favorites.Length != 1 || sheet[0] is not SheetHeader || Servers.AutoCard.Visibility != System.Windows.Visibility.Visible)
            throw new InvalidOperationException("Favorite star did not move the server into «Избранное» or was not captured for encrypted storage");
        Servers.SearchBox.Text = "no-synthetic-match";
        if (ProfilePicker.Items.Count != 0 || profile != selected || Servers.SheetList.Items.OfType<ServerRow>().Any())
            throw new InvalidOperationException("Empty search changed connection or kept rows");
        Servers.SearchBox.Clear();
        Servers.SortOrder.SelectedIndex = 1;
        if (ProfilePicker.Items.Count != 2 || profile != selected || engine.ProcessId is not null)
            throw new InvalidOperationException("Sorting changed selected connection or started a core");
        checks.Add("Favorites star, groups, search and sorting preserve the selected connection and do not start networking");
        measurements[Profiles.Fingerprint(selected)] = new(Profiles.Fingerprint(selected), DateTimeOffset.UtcNow.AddMinutes(-21), true, 12);
        RefreshServerLabels();
        if (!selected.ListStatus.Contains("устарело") || !selected.ListStatus.Contains("★"))
            throw new InvalidOperationException("Stale measurement or favorite marker missing");
        measurements.Clear();
        ToggleFavorite(Row(selected));
        Servers.SortOrder.SelectedIndex = 0;
        Navigate("Connection");
        if (Servers.Visibility == Visibility.Visible) throw new InvalidOperationException("Server sheet did not close");
        checks.Add("Server rows mark stale measurements and favorites without changing profile names");
        var report = CreateSupportReport();
        if (!report.Contains("Revision") || !report.Contains("LocalProxy") || report.Contains("example.com"))
            throw new InvalidOperationException("Support report omitted build identity or included a server address");
        checks.Add("Support report includes build identity and actual mode without server addresses");
    }
}

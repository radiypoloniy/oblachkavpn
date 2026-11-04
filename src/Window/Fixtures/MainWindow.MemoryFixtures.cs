using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckDeferredViews(List<string> checks)
    {
        RefreshHome(); ApplyLayout();
        if (picker is not null || doctor is not null || siteCheck is not null || analytics is not null)
            throw new InvalidOperationException("Passive navigation created deferred views");
        OpenAnalytics(); var first = analytics;
        CloseAnalytics(); OpenAnalytics();
        if (first is null || !ReferenceEquals(first, analytics) || analytics!.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Deferred analytics did not retain its instance");
        CloseAnalytics();
        OpenDoctor(); HidePage(doctor); OpenSiteCheck(); HidePage(siteCheck);
        if (doctor is null || siteCheck is null || picker is not null)
            throw new InvalidOperationException("Deferred diagnostics did not load independently");
        checks.Add("Rare pages are created only when opened; repeated navigation preserves state and handlers");
    }

    private void CheckLiveCache(List<string> checks)
    {
        const string path = @"C:\Apps\Memory\app.exe";
        for (var i = 0; i < 100; i++)
        {
            var apps = LiveApps.FromCore([new(path, false, $"site{i}.example.org", "192.0.2.1", 443, ["local-core"], "id")], _ => false);
            var rows = LiveTree(apps);
            TrimLiveCache(rows);
            if (liveSites.Count != 1 || liveRows.Count != 1)
                throw new InvalidOperationException("Expired site or program presentation remained cached");
        }
        var entries = Enumerable.Range(0, 16).Select(i => new LiveSite($"site{i:00}.example.org", 1, 0, 0, [$"id-{i}"])).ToArray();
        var browser = new LiveApp(path, entries.Length, 0, 0, []) { Sites = entries };
        var row = LiveRow(browser);
        if (row.Sites.Length != 8 || row.ConnectionIds.Length != entries.Length)
            throw new InvalidOperationException("Collapsed site preview lost connection IDs or grew beyond eight rows");
        row.Expanded = true;
        LiveRow(browser);
        if (row.Sites.Length != entries.Length || row.VisibleSites.Count != 12 || row.MoreSitesVisibility != Visibility.Visible)
            throw new InvalidOperationException("Expanded sites did not expose the full list with pagination");
        row.SiteQuery = "site15";
        if (row.VisibleSites.Count != 1 || row.VisibleSites[0].Name != "site15.example.org")
            throw new InvalidOperationException("Site search missed a connection beyond the visible page");
        checks.Add("Browser site search reaches connections beyond the preview; close-all retains every connection ID");
        var blocked = liveRows.Values.Single(); blocked.Rule = SiteAction.Block;
        ClearLivePresentation();
        if (liveSites.Count != 0 || !liveRows.Values.Contains(blocked))
            throw new InvalidOperationException("Cache cleanup lost blocked program or kept old site data");
        liveRows.Clear(); liveScopes.Clear();
        checks.Add("Connection presentation stays bounded across changing sites; blocked programs remain available");
    }

    private void CheckAppVirtualization(List<string> checks)
    {
        var view = new Views.AppPicker();
        var rows = Enumerable.Range(0, 2000).Select(i => new Views.AppRow(
            new InstalledApp("Fixture " + i, "fixture.exe", @"C:\Apps\Fixture\fixture.exe", false), null)).ToArray();
        rows[^1].Checked = true;
        view.Load(rows, []); view.Open([]);
        rows[^1].Checked = true;
        view.Measure(new Size(700, 600)); view.Arrange(new Rect(0, 0, 700, 600)); view.UpdateLayout();
        if (view.AppList.ItemContainerGenerator.ContainerFromIndex(0) is null ||
            view.AppList.ItemContainerGenerator.ContainerFromIndex(1999) is not null)
            throw new InvalidOperationException("Application picker created off-screen row containers");
        view.SearchBox.Text = "Fixture 1999";
        view.UpdateLayout();
        if (view.AppList.Items.Count != 1 || !ReferenceEquals(view.AppList.Items[0], rows[^1]) || !rows[^1].Checked)
            throw new InvalidOperationException("Virtualization lost selection or search results");
        checks.Add("Application picker virtualizes 2000 entries and preserves selections while searching");
    }
}

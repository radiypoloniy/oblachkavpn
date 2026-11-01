using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckResourceBounds(List<string> checks)
    {
        var profiles = Enumerable.Range(0, 600).Select(i => Profiles.Parse(
            $"vless://{i + 100:x8}-1111-4111-8111-111111111111@node{i}.invalid:443?security=tls&type=tcp#FI%20Server%20{i}")).ToArray();
        SetGroup(ProfileGroup.Manual, profiles); ApplyProfiles(false);
        var original = importedProfiles[0]; var key = Profiles.Fingerprint(original);
        measurements[key] = new(key, DateTimeOffset.UtcNow, true, 10);
        pings[key] = new(key, DateTimeOffset.UtcNow, true, 20);
        hiddenServers.Add(FavoriteKey(original)); ApplyProfiles(true);
        if (!measurements.ContainsKey(key) || !pings.ContainsKey(key)) throw new InvalidOperationException("Hidden server lost its measurements");
        hiddenServers.Clear(); ApplyProfiles(true);

        RenderAutoPool();
        var pool = Settings.AutoPool;
        pool.Visibility = Visibility.Visible;
        pool.Measure(new Size(700, 600)); pool.Arrange(new Rect(0, 0, 700, 600)); pool.UpdateLayout();
        if (pool.AutoPoolList.ItemContainerGenerator.ContainerFromIndex(0) is null ||
            pool.AutoPoolList.ItemContainerGenerator.ContainerFromIndex(599) is not null)
            throw new InvalidOperationException($"Auto pool containers: first={pool.AutoPoolList.ItemContainerGenerator.ContainerFromIndex(0) is not null}, last={pool.AutoPoolList.ItemContainerGenerator.ContainerFromIndex(599) is not null}");
        var row = (Views.AutoPoolRow)pool.AutoPoolList.Items[0];
        var toggle = Descendants<CheckBox>(pool.AutoPoolList).First(c => ReferenceEquals(c.DataContext, row));
        var included = autoPool.Contains(row.Profile);
        toggle.IsChecked = !included; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (autoPool.Contains(row.Profile) == included) throw new InvalidOperationException("Virtualized auto pool ignored toggle");
        toggle.IsChecked = included; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        checks.Add("Auto pool virtualizes 600 entries and applies toggle actions through recycled row data");

        var live = new Views.ConnectionsView();
        var rows = Enumerable.Range(0, 600).Select(i => new Views.ConnectionRow($@"C:\Apps\fixture-{i}.exe") { Name = "Fixture " + i }).ToArray();
        live.Load(rows);
        live.Measure(new Size(700, 600)); live.Arrange(new Rect(0, 0, 700, 600)); live.UpdateLayout();
        if (live.AppList.ItemContainerGenerator.ContainerFromIndex(0) is null || live.AppList.ItemContainerGenerator.ContainerFromIndex(599) is not null)
            throw new InvalidOperationException("Connections view realized off-screen programs");
        var scroll = Descendants<ScrollViewer>(live.AppList).First();
        scroll.ScrollToVerticalOffset(20); live.UpdateLayout();
        if (Math.Abs(scroll.VerticalOffset - 20) > 0.1 || live.AppList.ItemContainerGenerator.ContainerFromIndex(0) is null)
            throw new InvalidOperationException("Connection scrolling skipped a whole program instead of moving by pixels");
        live.SearchBox.Text = "Fixture 599"; live.UpdateLayout();
        if (live.AppList.Items.Count != 1 || !ReferenceEquals(live.AppList.Items[0], rows[^1]))
            throw new InvalidOperationException("Virtualized connection search lost its result");
        checks.Add("Connection list virtualizes 600 programs and preserves search");

        runningProfile = original;
        SetGroup(ProfileGroup.Manual, [profiles[^1]]); ApplyProfiles(false);
        if (!measurements.ContainsKey(key) || !pings.ContainsKey(key)) throw new InvalidOperationException("Active server lost its measurements");
        runningProfile = null; TrimMeasurements();
        if (measurements.ContainsKey(key) || pings.ContainsKey(key) || MachineOf(importedProfiles[0]).Count != 1)
            throw new InvalidOperationException("Old subscription data remained cached");
        groups.Clear(); groupProfiles.Clear(); ApplyProfiles(false);
        pool.AutoPoolList.ItemsSource = null;
        pool.Visibility = Visibility.Collapsed;
        checks.Add("Measurement cleanup preserves hidden and active profiles and releases removed profiles");
    }
}

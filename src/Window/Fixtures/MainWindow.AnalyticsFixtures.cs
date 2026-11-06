using System.Windows;
using System.Windows.Controls.Primitives;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckAnalytics(List<string> checks)
    {
        var width = Width;
        try
        {
            trafficHistory.Reset(); speedHistory.Reset();
            trafficHistory.Add(100, 900, DateTime.Now);
            trafficHistory.AddProgram(Environment.ProcessPath!, 100, 400, DateTime.Now, new(60, 240, 40, 160));
            trafficHistory.AddSource(new(new string('A', 64), "Тестовый VPN", "fixture", "Резерв", new string('C', 16)), 40, 160, DateTime.Now);
            trafficHistory.AddSource(new(new string('B', 64), "Тестовый VPN", "fixture", "Резерв", new string('C', 16)), 20, 80, DateTime.Now);
            var persisted = trafficHistory.Snapshot(DateTime.Now);
            var restored = new TrafficHistory();
            restored.Restore(persisted, null, 0, null, DateTime.Now);
            var today = restored.Report(DateTime.Now, 1);
            if (!TrafficHistory.Valid(persisted) || today.RoutePoints.Sum(p => p.Direct) != 200 ||
                !today.Sources.All(source => source.GroupName == "Резерв" && source.MachineId == new string('C', 16)))
                throw new InvalidOperationException("Saved analytics lost hourly routes or server attribution");
            speedHistory.Record(DateTime.Now, 100, 1000);
            Home.TodayCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            UpdateLayout();
            if (Analytics.Visibility != Visibility.Visible || Live.Visibility == Visibility.Visible || currentPage != "Analytics" ||
                Analytics.TotalText.Text != TrafficMeter.Size(1000) || !Ui.GetSelected(Home.TodayCard))
                throw new InvalidOperationException("Analytics did not open from the traffic card with current data");
            Analytics.PeriodButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var week = Analytics.PeriodItems.Children.OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Tag, 7));
            week.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (Analytics.PeriodDays != 7 || Analytics.Chart.Download?.Length != 7 || Analytics.ProgramList.Items.Count != 1)
                throw new InvalidOperationException("Analytics period or program report did not render");
            var program = (Views.AnalyticsProgramRow)Analytics.ProgramList.Items[0];
            if (program.Icon is null || program.PlaceholderVisibility != Visibility.Collapsed)
                throw new InvalidOperationException("Analytics did not load the program icon");
            if (Analytics.PeriodPopup.IsOpen || Analytics.RouteRing.Sample != new TrafficRouteSample(300, 200, 0) ||
                Analytics.Chart.Download?.Sum() != 300 ||
                Analytics.ServersChart.Items.Count != 1 ||
                Analytics.ServersChart.Items[0] is not Views.AnalyticsRank { Name: "Тестовый VPN" } ||
                Analytics.SubscriptionsChart.Items[0] is not Views.AnalyticsRank { Name: "Резерв" })
                throw new InvalidOperationException("Analytics did not render route and VPN-profile bytes");
            for (var i = 0; i < 26; i++) trafficHistory.AddProgram($@"C:\Fixture\test-{i:00}.exe", 1, 2, DateTime.Now);
            RefreshAnalytics();
            if (Analytics.ProgramList.Items.Count != 5)
                throw new InvalidOperationException("Analytics preview lost its five-row limit");
            Analytics.MorePrograms.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (Analytics.ProgramList.Items.Count != 25)
                throw new InvalidOperationException("Full program list is not paginated");
            Analytics.MorePrograms.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (Analytics.ProgramList.Items.Count != 2)
                throw new InvalidOperationException("The end of the program list is inaccessible");
            Analytics.ProgramSearch.Text = "test-25";
            if (Analytics.ProgramList.Items.Count != 1 || ((Views.AnalyticsProgramRow)Analytics.ProgramList.Items[0]).Name != "test-25")
                throw new InvalidOperationException("Analytics search did not search the full report");
            Analytics.ProgramSearch.Text = "";
            DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, "preview-analytics.png"));
            Navigate("Servers");
            if (Analytics.Visibility != Visibility.Collapsed || Servers.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Servers did not replace analytics");
            Home.TodayCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Home.LiveNav.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (Analytics.Visibility != Visibility.Collapsed || Live.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Live section did not replace analytics");
            Width = 460; OpenAnalytics();
            DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, "preview-analytics-compact.png")); ApplyLayout();
            if (!narrow || Analytics.BackButton.Visibility != Visibility.Visible || Island.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Compact analytics layout did not open over the sidebar");
            Analytics.BackButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (Analytics.Visibility != Visibility.Collapsed || Island.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Compact analytics back button did not return to the sidebar");
            checks.Add("Analytics opens with local reports, switches periods, replaces other pages and supports compact back navigation");
        }
        finally
        {
            trafficHistory.Reset(); programTraffic.Reset(); speedHistory.Reset();
            Width = width; Navigate("Connection"); UpdateLayout();
        }
    }
}

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
            trafficHistory.AddProgram(Environment.ProcessPath!, 100, 400, DateTime.Now);
            speedHistory.Record(DateTime.Now, 100, 1000);
            Home.TodayCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            UpdateLayout();
            if (Analytics.Visibility != Visibility.Visible || Live.Visibility == Visibility.Visible || currentPage != "Analytics" ||
                Analytics.TotalText.Text != TrafficMeter.Size(1000) || !Ui.GetSelected(Home.AnalyticsNav))
                throw new InvalidOperationException("Analytics did not open from the traffic card with current data");
            Analytics.Periods.SelectedIndex = 1;
            if (Analytics.PeriodDays != 7 || Analytics.Chart.Download?.Length != 7 || Analytics.ProgramList.Items.Count != 1)
                throw new InvalidOperationException("Analytics period or program report did not render");
            var program = (Views.AnalyticsProgramRow)Analytics.ProgramList.Items[0];
            if (program.Icon is null || program.PlaceholderVisibility != Visibility.Collapsed)
                throw new InvalidOperationException("Analytics did not load the program icon");
            DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, "preview-analytics.png"));
            Navigate("Servers");
            if (Analytics.Visibility != Visibility.Collapsed || Servers.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Servers did not replace analytics");
            Home.AnalyticsNav.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
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

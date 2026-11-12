using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Oblachka.Views;

public sealed record AnalyticsRank(string Name, string Amount, double Percent);

public partial class AnalyticsView
{
    private static readonly (int Days, string Label)[] PeriodOptions =
        [(1, "Сегодня"), (7, "Последние 7 дней"), (30, "Последние 30 дней")];
    private AnalyticsPoint[] axis = [];
    private TrafficRouteSample[] timeline = [];
    private TrafficSourceStats[] lastSources = [];
    private int interval = -1;

    private void WireDashboard()
    {
        PeriodButton.Click += (_, _) => OpenPeriods();
        PeriodPopup.Opened += (_, _) => PeriodItems.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Tag, PeriodDays))?.Focus();
        PeriodItems.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            ClosePeriods();
            args.Handled = true;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) PeriodPopup.IsOpen = false; };
        Chart.MouseLeftButtonDown += (_, args) =>
        {
            if (timeline.Length == 0 || Chart.ActualWidth <= 0) return;
            Chart.Focus();
            SelectInterval((int)(args.GetPosition(Chart).X * timeline.Length / Chart.ActualWidth));
        };
        Chart.KeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Left or Key.Right) || timeline.Length == 0) return;
            SelectInterval(interval + (args.Key == Key.Left ? -1 : 1));
            args.Handled = true;
        };
    }

    private void OpenPeriods()
    {
        PeriodItems.Children.Clear();
        foreach (var (days, label) in PeriodOptions)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel();
            if (PeriodDays == days)
            {
                var check = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IcoCheck"),
                    Style = (Style)FindResource("Icon"), Width = 14, Height = 14 };
                DockPanel.SetDock(check, Dock.Right);
                row.Children.Add(check);
                text.FontWeight = FontWeights.SemiBold;
            }
            row.Children.Add(text);
            var button = new Button { Style = (Style)FindResource("MenuRow"), Content = row, Tag = days };
            System.Windows.Automation.AutomationProperties.SetName(button, label + (days == PeriodDays ? ", выбран" : ""));
            button.Click += (_, _) => { ClosePeriods(); SetPeriod(days); };
            PeriodItems.Children.Add(button);
        }
        PeriodPopup.HorizontalOffset = PeriodButton.ActualWidth - 207;
        PeriodPopup.IsOpen = true;
    }

    private void ClosePeriods()
    {
        PeriodPopup.IsOpen = false;
        PeriodButton.Focus();
    }

    internal void SetPeriod(int days)
    {
        if (PeriodDays == days) return;
        PeriodDays = days;
        programOffset = 0;
        interval = -1;
        Chart.SelectedIndex = -1;
        ReportCaption.Text = PeriodOptions.Single(option => option.Days == days).Label;
        PeriodChanged?.Invoke(days);
    }

    private void RenderDashboard(AnalyticsReport report)
    {
        var routes = report.Programs.Aggregate(default(TrafficRouteSample),
            (sum, program) => sum + TrafficRouteSample.From(program.Total, program.Routes));
        RouteRing.Sample = routes;
        VpnShare.Text = routes.Total == 0 ? "—" : $"{(double)routes.Vpn / routes.Total:P0}";
        VpnText.Text = "Через VPN  " + Amount(routes.Vpn);
        DirectText.Text = "Напрямую  " + Amount(routes.Direct);
        UnknownText.Text = "Маршрут не определён  " + Amount(routes.Unknown);
        RouteCoverage.Text = routes.Total == 0 ? "" : $"Маршрут известен для {(double)(routes.Vpn + routes.Direct) / routes.Total:P0} трафика";
        const string explanation = "Доля VPN рассчитана от всего учтённого объёма, включая неизвестный маршрут. " +
            "В старой истории и у части источников нет разбивки. Промежуточный прокси сам по себе не подтверждает VPN. " +
            "Неизвестный маршрут не означает утечку и не подтверждает защиту. Для проверки нового учёта выберите «Сегодня».";
        UnknownText.ToolTip = RouteCoverage.ToolTip = VpnShare.ToolTip = explanation;
        var sameAxis = axis.Select(p => p.Label).SequenceEqual(report.Points.Select(p => p.Label));
        if (!sameAxis) { axis = report.Points; RenderAxis(axis); }
        if (!timeline.SequenceEqual(report.RoutePoints) || !sameAxis)
        {
            timeline = report.RoutePoints;
            Chart.Download = timeline.Select(p => p.Vpn).ToArray();
            Chart.Upload = timeline.Select(p => p.Direct).ToArray();
            Chart.Unclassified = timeline.Select(p => p.Unknown).ToArray();
            var peak = timeline.Select(p => p.Total).DefaultIfEmpty().Max();
            ChartEmpty.Visibility = peak > 0 ? Visibility.Collapsed : Visibility.Visible;
            ChartScale.Text = peak > 0 ? Amount(peak) : "";
            ChartDetail.Text = peak > 0 ? "Выберите столбик, чтобы увидеть объём." : "";
            if (peak > 0 && interval >= 0) SelectInterval(interval);
        }
        RenderRankings(report.Sources);
    }

    private void SelectInterval(int index)
    {
        if (timeline.Length == 0 || axis.Length != timeline.Length) return;
        interval = Math.Clamp(index, 0, timeline.Length - 1);
        Chart.SelectedIndex = interval;
        var point = timeline[interval];
        var label = axis[interval].Label + (PeriodDays == 1 ? ":00" : "");
        ChartDetail.Text = label + " · " + Amount(point.Total) + " · VPN " + Amount(point.Vpn) +
            " · напрямую " + Amount(point.Direct) + " · маршрут не определён " + Amount(point.Unknown);
    }

    private void RenderRankings(TrafficSourceStats[] sources)
    {
        if (lastSources.SequenceEqual(sources)) return;
        lastSources = sources;
        var all = sources.Sum(s => s.Total);
        var subscriptions = sources.GroupBy(s => s.GroupName is null ? "" : s.GroupId)
            .Select(g => (Name: g.First().GroupName ?? "Подписка не определена", Bytes: g.Sum(s => s.Total)));
        var machines = sources.GroupBy(s => s.MachineId ?? s.Id)
            .Select(g => (Source: g.First(), Bytes: g.Sum(s => s.Total))).ToArray();
        var repeats = machines.GroupBy(s => s.Source.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var servers = machines.Select(s => (Name: repeats.Contains(s.Source.Name) && s.Source.GroupName is { } group
            ? $"{s.Source.Name} · {group}" : s.Source.Name, s.Bytes));
        SubscriptionsChart.ItemsSource = Rank(subscriptions, all);
        ServersChart.ItemsSource = Rank(servers, all);
        SubscriptionsEmpty.Visibility = ServersEmpty.Visibility = all > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static AnalyticsRank[] Rank(IEnumerable<(string Name, long Bytes)> rows, long total)
    {
        var ordered = rows.Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).ToArray();
        var shown = ordered.Take(5);
        if (ordered.Length > 5) shown = shown.Append(("Остальные", ordered.Skip(5).Sum(x => x.Bytes)));
        return shown.Select(x => new AnalyticsRank(x.Name, Amount(x.Bytes),
            total > 0 ? 100d * x.Bytes / total : 0)).ToArray();
    }

    private static string Amount(long bytes) => bytes < 1024 ? $"{bytes} Б" : TrafficMeter.Size(bytes);
}

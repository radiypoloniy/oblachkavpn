using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Oblachka.Views;

public sealed record AnalyticsProgramRow(string Name, string Initial, string Detail, string Total, ImageSource? Icon)
{
    public Visibility PlaceholderVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

public partial class AnalyticsView : UserControl
{
    public event Action? Closed;
    public event Action? ExportRequested;
    public event Action? ResetRequested;
    public event Action<int>? PeriodChanged;
    public int PeriodDays { get; private set; } = 1;
    private string programsShown = "";

    public AnalyticsView()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => Closed?.Invoke();
        ExportButton.Click += (_, _) => ExportRequested?.Invoke();
        ResetButton.Click += (_, _) => ResetRequested?.Invoke();
        Periods.SelectionChanged += (_, _) =>
        {
            PeriodDays = Periods.SelectedIndex switch { 1 => 7, 2 => 30, _ => 1 };
            ReportCaption.Text = PeriodDays switch { 7 => "Последние 7 дней", 30 => "Последние 30 дней", _ => "Сегодня" };
            PeriodChanged?.Invoke(PeriodDays);
        };
        SizeChanged += (_, args) => Fit(args.NewSize.Width);
    }

    public void Show(bool focus)
    {
        Visibility = Visibility.Visible;
        if (focus) Periods.Focus();
    }

    public void Render(AnalyticsReport report, AnalyticsPoint[] speed, long currentUp, long currentDown, string server, SubscriptionMetadata? metadata)
    {
        TotalText.Text = TrafficMeter.Size(report.Total); DownloadText.Text = TrafficMeter.Size(report.Download); UploadText.Text = TrafficMeter.Size(report.Upload);
        CurrentDown.Text = TrafficMeter.Rate(currentDown); CurrentUp.Text = TrafficMeter.Rate(currentUp);
        ServerText.Text = server;
        Chart.Upload = report.Points.Select(p => p.Upload).ToArray(); Chart.Download = report.Points.Select(p => p.Download).ToArray();
        ChartEmpty.Visibility = report.Points.All(p => p.Total == 0) ? Visibility.Visible : Visibility.Collapsed;
        SpeedChart.Upload = speed.Select(p => p.Upload).ToArray(); SpeedChart.Download = speed.Select(p => p.Download).ToArray();
        RenderAxis(report.Points);
        var rows = report.Programs.Take(5).Select(p =>
        {
            var name = Path.GetFileNameWithoutExtension(p.Path);
            return new AnalyticsProgramRow(name, name.Length == 0 ? "?" : char.ToUpper(name[0]).ToString(),
                "↓ " + TrafficMeter.Size(p.Download) + " · ↑ " + TrafficMeter.Size(p.Upload), TrafficMeter.Size(p.Total), AppIcons.For(p.Path));
        }).ToArray();
        var signature = string.Join("|", rows.Select(r => r.Name + r.Detail + r.Total));
        if (signature != programsShown) { programsShown = signature; ProgramList.ItemsSource = rows; }
        ProgramsEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlanText.Text = metadata?.ShortSummary(DateTimeOffset.Now) ?? "Нет данных о подписке";
        var (remaining, used) = metadata?.Remaining() ?? (null, null);
        PlanRemaining.Text = remaining ?? (metadata is null ? "Подписка не добавлена или не сообщает эти сведения." : "Лимит трафика не указан.");
        PlanBars.Filled = used is { } value ? (int)Math.Ceiling(value * PlanBars.Count) : 0;
    }

    private void RenderAxis(AnalyticsPoint[] points)
    {
        AxisLabels.Children.Clear(); AxisLabels.ColumnDefinitions.Clear();
        for (var i = 0; i < points.Length; i++)
        {
            AxisLabels.ColumnDefinitions.Add(new ColumnDefinition());
            var show = points.Length <= 7 || points.Length == 24 && i % 3 == 0 || points.Length == 30 && (i % 5 == 0 || i == points.Length - 1);
            if (!show) continue;
            var label = new TextBlock { Text = points[i].Label, Foreground = (System.Windows.Media.Brush)FindResource("Fa"), FontSize = (double)FindResource("FsCaption"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, i); AxisLabels.Children.Add(label);
        }
    }

    private void Fit(double width)
    {
        var narrow = width < 560;
        PlanColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(PlanCard, narrow ? 0 : 1); Grid.SetRow(PlanCard, narrow ? 1 : 0);
        PlanCard.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(5, 0, 0, 0);
        ProgramsCard.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 5, 0);
    }
}

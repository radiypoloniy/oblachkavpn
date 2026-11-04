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
    private AnalyticsProgram[] programs = [];
    private const int PreviewPrograms = 5, ProgramPageSize = 25;
    private int programOffset;

    public AnalyticsView()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => Closed?.Invoke();
        ExportButton.Click += (_, _) => ExportRequested?.Invoke();
        ResetButton.Click += (_, _) => ResetRequested?.Invoke();
        MorePrograms.Click += (_, _) =>
        {
            if (ProgramSearch.IsVisible) programOffset += ProgramPageSize;
            else ProgramSearch.Visibility = Visibility.Visible;
            RenderPrograms();
        };
        PreviousPrograms.Click += (_, _) => { programOffset -= ProgramPageSize; RenderPrograms(); };
        ProgramSearch.TextChanged += (_, _) =>
        {
            programOffset = 0;
            RenderPrograms();
        };
        Periods.SelectionChanged += (_, _) =>
        {
            PeriodDays = Periods.SelectedIndex switch { 1 => 7, 2 => 30, _ => 1 };
            programOffset = 0;
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

    public void Render(AnalyticsReport report, AnalyticsPoint[] speed, long currentUp, long currentDown, string server, SubscriptionState? subscription)
    {
        TotalText.Text = TrafficMeter.Size(report.Total); DownloadText.Text = TrafficMeter.Size(report.Download); UploadText.Text = TrafficMeter.Size(report.Upload);
        CurrentDown.Text = TrafficMeter.Rate(currentDown); CurrentUp.Text = TrafficMeter.Rate(currentUp);
        ServerText.Text = server;
        Chart.Upload = report.Points.Select(p => p.Upload).ToArray(); Chart.Download = report.Points.Select(p => p.Download).ToArray();
        ChartEmpty.Visibility = report.Points.All(p => p.Total == 0) ? Visibility.Visible : Visibility.Collapsed;
        SpeedChart.Upload = speed.Select(p => p.Upload).ToArray(); SpeedChart.Download = speed.Select(p => p.Download).ToArray();
        RenderAxis(report.Points);
        programs = report.Programs;
        RenderPrograms();
        PlanCard.Visibility = subscription is null ? Visibility.Collapsed : Visibility.Visible;
        PlanTitle.Text = subscription?.Group.Name ?? "";
        PlanSource.Text = subscription?.UpdatedText ?? "";
        PlanText.Text = subscription?.TermText ?? "";
        PlanRemaining.Text = subscription?.TrafficText ?? "";
        PlanBars.Visibility = subscription?.Used is null ? Visibility.Collapsed : Visibility.Visible;
        PlanBars.Filled = subscription?.Used is { } value ? (int)Math.Ceiling(value * PlanBars.Count) : 0;
    }

    private void RenderPrograms()
    {
        var query = ProgramSearch.Text.Trim();
        var all = ProgramSearch.Visibility == Visibility.Visible;
        var limit = all ? ProgramPageSize : PreviewPrograms;
        var matches = query.Length == 0 ? programs : programs.Where(p => p.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (programOffset >= matches.Length) programOffset = 0;
        var shown = matches.Skip(programOffset).Take(limit).ToArray();
        var signature = string.Join("\n", shown.Select(p => $"{p.Path}|{p.Upload}|{p.Download}"));
        if (signature != programsShown)
        {
            programsShown = signature;
            ProgramList.ItemsSource = shown.Select(p =>
            {
                var name = Path.GetFileNameWithoutExtension(p.Path);
                return new AnalyticsProgramRow(name, name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString(),
                    "↓ " + TrafficMeter.Size(p.Download) + " · ↑ " + TrafficMeter.Size(p.Upload), TrafficMeter.Size(p.Total), AppIcons.For(p.Path));
            }).ToArray();
        }
        ProgramsEmpty.Text = query.Length == 0 ? "Данные появятся после подключения в режиме «Весь компьютер»." : "Программы не найдены.";
        ProgramsEmpty.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviousPrograms.Visibility = programOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
        MorePrograms.Content = all ? "Далее" : "Все программы";
        MorePrograms.Visibility = matches.Length > programOffset + limit ? Visibility.Visible : Visibility.Collapsed;
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

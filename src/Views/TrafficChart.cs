using System.Windows;
using System.Windows.Media;

namespace Oblachka.Views;

// График аналитики рисуется одним контролом: при смене периода не пересобираются сотни элементов.
public sealed class TrafficChart : FrameworkElement
{
    public static readonly DependencyProperty UploadProperty = Register<long[]?>(nameof(Upload), null);
    public static readonly DependencyProperty DownloadProperty = Register<long[]?>(nameof(Download), null);
    public static readonly DependencyProperty LineProperty = Register(nameof(Line), false);
    public static readonly DependencyProperty UploadBrushProperty = Register<Brush?>(nameof(UploadBrush), null);
    public static readonly DependencyProperty DownloadBrushProperty = Register<Brush?>(nameof(DownloadBrush), null);
    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);

    public long[]? Upload { get => (long[]?)GetValue(UploadProperty); set => SetValue(UploadProperty, value); }
    public long[]? Download { get => (long[]?)GetValue(DownloadProperty); set => SetValue(DownloadProperty, value); }
    public bool Line { get => (bool)GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public Brush? UploadBrush { get => (Brush?)GetValue(UploadBrushProperty); set => SetValue(UploadBrushProperty, value); }
    public Brush? DownloadBrush { get => (Brush?)GetValue(DownloadBrushProperty); set => SetValue(DownloadBrushProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(TrafficChart), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth; var height = ActualHeight;
        if (width <= 2 || height <= 2) return;
        var grid = GridBrush is null ? null : new Pen(GridBrush, 1);
        if (grid is not null)
            for (var row = 1; row <= 3; row++)
            {
                var y = Math.Round(height * row / 4) + 0.5;
                dc.DrawLine(grid, new Point(0, y), new Point(width, y));
            }
        var up = Upload ?? []; var down = Download ?? [];
        var count = Math.Max(up.Length, down.Length);
        if (count == 0) return;
        var totals = Enumerable.Range(0, count).Select(i => (i < up.Length ? up[i] : 0) + (i < down.Length ? down[i] : 0)).ToArray();
        var max = Math.Max(1, totals.Max());
        if (Line) { DrawLine(dc, up, width, height, max, UploadBrush); DrawLine(dc, down, width, height, max, DownloadBrush); return; }
        var step = width / count;
        var bar = Math.Max(1, Math.Min(20, step * 0.62));
        for (var i = 0; i < count; i++)
        {
            if (totals[i] <= 0) continue;
            var x = i * step + (step - bar) / 2;
            var totalHeight = Math.Max(2, (height - 2) * totals[i] / max);
            var downHeight = totals[i] == 0 ? 0 : totalHeight * (i < down.Length ? down[i] : 0) / totals[i];
            if (downHeight > 0) dc.DrawRectangle(DownloadBrush, null, new Rect(x, height - downHeight, bar, downHeight));
            if (totalHeight - downHeight > 0) dc.DrawRectangle(UploadBrush, null, new Rect(x, height - totalHeight, bar, totalHeight - downHeight));
        }
    }

    private static void DrawLine(DrawingContext dc, long[] values, double width, double height, long max, Brush? brush)
    {
        if (values.Length == 0 || brush is null) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var point = new Point(values.Length == 1 ? width / 2 : i * width / (values.Length - 1), height - 2 - (height - 6) * values[i] / max);
                if (i == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}

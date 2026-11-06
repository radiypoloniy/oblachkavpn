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
    public static readonly DependencyProperty UnclassifiedProperty = Register<long[]?>(nameof(Unclassified), null);
    public static readonly DependencyProperty UnclassifiedBrushProperty = Register<Brush?>(nameof(UnclassifiedBrush), null);
    public static readonly DependencyProperty SelectedIndexProperty = Register(nameof(SelectedIndex), -1);
    public static readonly DependencyProperty SelectionBrushProperty = Register<Brush?>(nameof(SelectionBrush), null);

    public long[]? Upload { get => (long[]?)GetValue(UploadProperty); set => SetValue(UploadProperty, value); }
    public long[]? Download { get => (long[]?)GetValue(DownloadProperty); set => SetValue(DownloadProperty, value); }
    public bool Line { get => (bool)GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public Brush? UploadBrush { get => (Brush?)GetValue(UploadBrushProperty); set => SetValue(UploadBrushProperty, value); }
    public Brush? DownloadBrush { get => (Brush?)GetValue(DownloadBrushProperty); set => SetValue(DownloadBrushProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public long[]? Unclassified { get => (long[]?)GetValue(UnclassifiedProperty); set => SetValue(UnclassifiedProperty, value); }
    public Brush? UnclassifiedBrush { get => (Brush?)GetValue(UnclassifiedBrushProperty); set => SetValue(UnclassifiedBrushProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public Brush? SelectionBrush { get => (Brush?)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(TrafficChart), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth; var height = ActualHeight;
        if (width <= 2 || height <= 2) return;
        if (Focusable) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var grid = GridBrush is null ? null : new Pen(GridBrush, 1);
        if (grid is not null)
            for (var row = 1; row <= 3; row++)
            {
                var y = Math.Round(height * row / 4) + 0.5;
                dc.DrawLine(grid, new Point(0, y), new Point(width, y));
            }
        var up = Upload ?? []; var down = Download ?? [];
        var unknown = Unclassified ?? [];
        var count = Math.Max(unknown.Length, Math.Max(up.Length, down.Length));
        if (count == 0) return;
        var totals = Enumerable.Range(0, count).Select(i => At(up, i) + At(down, i) + At(unknown, i)).ToArray();
        var max = Math.Max(1, totals.Max());
        if (Line) { DrawLine(dc, up, width, height, max, UploadBrush); DrawLine(dc, down, width, height, max, DownloadBrush); return; }
        var step = width / count;
        var bar = Math.Max(1, Math.Min(20, step * 0.62));
        if (SelectedIndex >= 0 && SelectedIndex < count)
            dc.DrawRectangle(SelectionBrush, null, new Rect(SelectedIndex * step, 0, step, height));
        for (var i = 0; i < count; i++)
        {
            if (totals[i] <= 0) continue;
            var x = i * step + (step - bar) / 2;
            var totalHeight = Math.Max(2, (height - 2) * totals[i] / max);
            var bottom = height;
            DrawSegment(At(down, i), DownloadBrush);
            DrawSegment(At(up, i), UploadBrush);
            DrawSegment(At(unknown, i), UnclassifiedBrush);
            void DrawSegment(long value, Brush? brush)
            {
                if (value <= 0) return;
                var segment = totalHeight * value / totals[i];
                bottom -= segment;
                dc.DrawRectangle(brush, null, new Rect(x, bottom, bar, segment));
            }
        }
    }

    private static long At(long[] values, int index) => index < values.Length ? values[index] : 0;

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

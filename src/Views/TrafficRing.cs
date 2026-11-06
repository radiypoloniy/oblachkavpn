using System.Windows;
using System.Windows.Media;

namespace Oblachka.Views;

public sealed class TrafficRing : FrameworkElement
{
    public static readonly DependencyProperty SampleProperty = DependencyProperty.Register(nameof(Sample),
        typeof(TrafficRouteSample), typeof(TrafficRing), new FrameworkPropertyMetadata(default(TrafficRouteSample), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VpnBrushProperty = RegisterBrush(nameof(VpnBrush));
    public static readonly DependencyProperty DirectBrushProperty = RegisterBrush(nameof(DirectBrush));
    public static readonly DependencyProperty UnknownBrushProperty = RegisterBrush(nameof(UnknownBrush));
    public static readonly DependencyProperty EmptyBrushProperty = RegisterBrush(nameof(EmptyBrush));

    public TrafficRouteSample Sample { get => (TrafficRouteSample)GetValue(SampleProperty); set => SetValue(SampleProperty, value); }
    public Brush? VpnBrush { get => (Brush?)GetValue(VpnBrushProperty); set => SetValue(VpnBrushProperty, value); }
    public Brush? DirectBrush { get => (Brush?)GetValue(DirectBrushProperty); set => SetValue(DirectBrushProperty, value); }
    public Brush? UnknownBrush { get => (Brush?)GetValue(UnknownBrushProperty); set => SetValue(UnknownBrushProperty, value); }
    public Brush? EmptyBrush { get => (Brush?)GetValue(EmptyBrushProperty); set => SetValue(EmptyBrushProperty, value); }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(name,
        typeof(Brush), typeof(TrafficRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(center.X, center.Y) - 7;
        if (radius <= 0) return;
        var sample = Sample;
        if (sample.Total <= 0)
        {
            dc.DrawEllipse(null, new Pen(EmptyBrush, 12), center, radius, radius);
            return;
        }
        var angle = -90d;
        foreach (var (value, brush) in new[] { (sample.Vpn, VpnBrush), (sample.Direct, DirectBrush), (sample.Unknown, UnknownBrush) })
        {
            if (value <= 0) continue;
            var pen = new Pen(brush, 12);
            if (value == sample.Total) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
            var sweep = 360d * value / sample.Total;
            Point At(double degrees) => new(center.X + radius * Math.Cos(degrees * Math.PI / 180),
                center.Y + radius * Math.Sin(degrees * Math.PI / 180));
            var path = new StreamGeometry();
            using (var ctx = path.Open())
            {
                ctx.BeginFigure(At(angle), false, false);
                ctx.ArcTo(At(angle + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, pen, path);
            angle += sweep;
        }
    }
}

using System.Windows;
using System.Windows.Media;

namespace Oblachka.Views;

public enum PixelKind { Steps, Columns, Segments }

// Индикаторы рисуются одним элементом: столбики трафика и сегменты расхода подписки.
public sealed class PixelBars : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = Register(nameof(Kind), PixelKind.Steps);
    public static readonly DependencyProperty FillProperty = Register<Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty EmptyProperty = Register<Brush?>(nameof(Empty), null);
    public static readonly DependencyProperty AccentProperty = Register<Brush?>(nameof(Accent), null);
    // Steps: сколько ступеней из трёх горит. Segments: сколько сегментов из Count закрашено.
    public static readonly DependencyProperty FilledProperty = Register(nameof(Filled), 0);
    public static readonly DependencyProperty CountProperty = Register(nameof(Count), 6);
    // Columns: высоты столбиков 0…1; последний — «сейчас», цветом Accent.
    public static readonly DependencyProperty ValuesProperty = Register<double[]?>(nameof(Values), null);
    public const int Rows = 6;

    public PixelKind Kind { get => (PixelKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Empty { get => (Brush?)GetValue(EmptyProperty); set => SetValue(EmptyProperty, value); }
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public int Filled { get => (int)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public int Count { get => (int)GetValue(CountProperty); set => SetValue(CountProperty, value); }
    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(PixelBars), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        switch (Kind)
        {
            case PixelKind.Steps:
            {
                // Три ступени: 3/7, 5/7 и полная высота; промежуток — треть ширины ступени.
                var step = w / 3.6;
                for (var i = 0; i < 3; i++)
                {
                    var height = h * (3 + 2 * i) / 7;
                    dc.DrawRectangle(i < Filled ? Fill : Empty, null, new Rect(i * step * 1.3, h - height, step, height));
                }
                break;
            }
            case PixelKind.Segments:
            {
                var n = Math.Max(1, Count);
                var size = w / (n + (n - 1) * 0.2);
                for (var i = 0; i < n; i++)
                    dc.DrawRectangle(i < Filled ? Fill : Empty, null, new Rect(i * size * 1.2, 0, size, h));
                break;
            }
            case PixelKind.Columns:
            {
                var values = Values ?? [];
                if (values.Length == 0) return;
                var slot = w / values.Length;
                var barWidth = slot * 0.64;
                var radius = Math.Min(2, barWidth / 2);
                for (var i = 0; i < values.Length; i++)
                {
                    var x = (i + 0.18) * slot;
                    var height = Math.Clamp(values[i] * h, Math.Min(2, h), h);
                    var brush = values[i] <= 0 ? Empty : i == values.Length - 1 ? Accent ?? Fill : Fill;
                    dc.DrawRoundedRectangle(brush, null, new Rect(x, h - height, barWidth, height), radius, radius);
                }
                break;
            }
        }
    }
}

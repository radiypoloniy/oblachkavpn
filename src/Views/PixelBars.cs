using System.Windows;
using System.Windows.Media;

namespace Oblachka.Views;

public enum PixelKind { Steps, Columns, Segments }

// Индикаторы из квадратов (облик 0.20): пинг сервера ступенями, трафик по часам столбиками из
// квадратов, израсходованное по подписке — полосой из сегментов. Рисуются сами (OnRender): в
// столбиках за день сотня квадратов, отдельными элементами это лишняя работа раскладке.
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
                var cell = Math.Min(w / (values.Length + (values.Length - 1) * 0.4), h / (Rows + (Rows - 1) * 0.33));
                var gapX = values.Length > 1 ? (w - cell * values.Length) / (values.Length - 1) : 0;
                var gapY = (h - cell * Rows) / (Rows - 1);
                for (var c = 0; c < values.Length; c++)
                {
                    var lit = values[c] <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(values[c] * Rows), 1, Rows);
                    var brush = c == values.Length - 1 ? Accent ?? Fill : Fill;
                    for (var r = 0; r < Rows; r++)
                        dc.DrawRectangle(r < lit ? brush : Empty, null, new Rect(c * (cell + gapX), h - (r + 1) * cell - r * gapY, cell, cell));
                }
                break;
            }
        }
    }
}

using System.Windows;
using System.Windows.Media;

namespace Oblachka;

// Токены оформления для кода: те же ресурсы, что у разметки (theme/tokens.json → src/Themes).
// Цвета, размеры текста и скругления в коде берутся только отсюда — scripts/check-tokens.ps1
// не пропустит Color.FromRgb, «#RRGGBB» и числа в FontSize/CornerRadius.
public static class Tokens
{
    public static Brush Brush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    public static Color Color(string key) => Application.Current?.TryFindResource(key) is Color color ? color : Colors.Transparent;
    public static double Size(string key) => Application.Current?.TryFindResource(key) is double size ? size : 13;
    public static CornerRadius Radius(string key) => Application.Current?.TryFindResource(key) is CornerRadius radius ? radius : new CornerRadius();
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Oblachka.Views;

// Боковая панель окна. Логика — в MainWindow (Home, States, Connection): здесь только вид.
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        // Border в WPF не обрезает содержимое по скруглению — небо обрезается своей геометрией.
        SkyClip.SizeChanged += (_, e) => SkyClip.Clip = new RectangleGeometry(new Rect(e.NewSize), 18, 18);
    }
}

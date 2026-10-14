using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Oblachka.Views;

public partial class ServersSheet : UserControl
{
    public ServersSheet()
    {
        InitializeComponent();
        AddButton.Click += (_, _) => AddPanel.Visibility = AddPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        RenderTransform = new TranslateTransform();
    }

    // Выезжает поверх главного окна: сдвиг 24 → 0 и проявление за 240 мс.
    public void Open()
    {
        AddPanel.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ((TranslateTransform)RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Oblachka.Views;

public enum PowerState { Off, Connecting, On, Blocked, Disabled }

// Круглая кнопка подключения (PowerSwitch.xaml): состояние — цветом слоя, переход — наплывом.
public partial class PowerSwitch : UserControl
{
    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(300));
    private PowerState state = PowerState.Off;

    public event RoutedEventHandler? Click;

    public PowerSwitch()
    {
        InitializeComponent();
        Apply(animate: false);
    }

    public PowerState State
    {
        get => state;
        set { if (state == value) return; state = value; Apply(animate: true); }
    }

    private void SurfaceClick(object sender, RoutedEventArgs args)
    {
        if (state != PowerState.Disabled) Click?.Invoke(this, args);
    }

    private void Apply(bool animate)
    {
        var (mid, on, blocked, glyph, icon, name) = state switch
        {
            PowerState.Connecting => (1.0, 0.0, 0.0, "OnAccent", "IcoPower", "Подключаемся, нажмите, чтобы отменить"),
            PowerState.On => (0.0, 1.0, 0.0, "OnAccent", "IcoPower", "Выключить VPN"),
            PowerState.Blocked => (0.0, 0.0, 1.0, "OnAccent", "IcoLock", "Выключить VPN и открыть интернет"),
            _ => (0.0, 0.0, 0.0, "Mu", "IcoPower", "Включить VPN")
        };
        Animate(MidLayer, OpacityProperty, mid, animate);
        Animate(OnLayer, OpacityProperty, on, animate);
        Animate(Glow, OpacityProperty, on, animate);
        Animate(BlockedLayer, OpacityProperty, blocked, animate);
        Animate(Ring, OpacityProperty, mid, animate);
        Glyph.SetResourceReference(Shape.StrokeProperty, glyph);
        Glyph.Data = (Geometry)FindResource(icon);
        Opacity = state == PowerState.Disabled ? 0.6 : 1;
        Surface.Cursor = state == PowerState.Disabled ? null : System.Windows.Input.Cursors.Hand;
        AutomationProperties.SetName(Surface, name);
        // Пока подключаемся, по кругу бежит кольцо, а значок дышит. При выключенной анимации
        // Windows кольцо стоит на месте: состояние видно по цвету.
        if (state == PowerState.Connecting && SystemParameters.ClientAreaAnimation)
        {
            Spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            var pulse = new DoubleAnimation(1, 0.9, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Pulse.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            Pulse.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
        else
        {
            Spin.BeginAnimation(RotateTransform.AngleProperty, null);
            Pulse.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Pulse.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, bool animate)
    {
        if (!animate) { target.BeginAnimation(property, null); ((DependencyObject)target).SetValue(property, to); return; }
        target.BeginAnimation(property, new DoubleAnimation(to, Fade) { FillBehavior = FillBehavior.HoldEnd });
    }
}

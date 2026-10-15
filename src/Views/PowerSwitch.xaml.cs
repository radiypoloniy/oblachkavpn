using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Oblachka.Views;

public enum PowerState { Off, Connecting, On, Blocked, Disabled }

public partial class PowerSwitch : UserControl
{
    // Положение ручки: 8 → 44 → 80 px от левого края, 350 мс, cubic-bezier(.4,.1,.2,1).
    private static readonly KeySpline Ease = new(0.4, 0.1, 0.2, 1);
    private static readonly Duration Move = new(TimeSpan.FromMilliseconds(350));
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
        var (x, mid, on, blocked, glyph, icon, name) = state switch
        {
            PowerState.Connecting => (36.0, 1.0, 0.0, 0.0, "MidI", "IcoPower", "Подключаемся, нажмите, чтобы отменить"),
            PowerState.On => (72.0, 0.0, 1.0, 0.0, "Ac", "IcoPower", "Выключить VPN"),
            PowerState.Blocked => (72.0, 0.0, 0.0, 1.0, "EI", "IcoLock", "Выключить VPN и открыть интернет"),
            _ => (0.0, 0.0, 0.0, 0.0, "Fa", "IcoPower", "Включить VPN")
        };
        Animate(Slide, TranslateTransform.XProperty, x, animate);
        Animate(MidLayer, OpacityProperty, mid, animate);
        Animate(OnLayer, OpacityProperty, on, animate);
        Animate(Glow, OpacityProperty, on, animate);
        Animate(BlockedLayer, OpacityProperty, blocked, animate);
        Glyph.SetResourceReference(Shape.StrokeProperty, glyph);
        Glyph.Data = (Geometry)FindResource(icon);
        Opacity = state == PowerState.Disabled ? 0.6 : 1;
        Surface.Cursor = state == PowerState.Disabled ? null : System.Windows.Input.Cursors.Hand;
        AutomationProperties.SetName(Surface, name);
        // Пока подключаемся, ручка дышит: 1 → 0.94 → 1 за 1,2 с.
        if (state == PowerState.Connecting)
        {
            var pulse = new DoubleAnimation(1, 0.94, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Pulse.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            Pulse.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
        else
        {
            Pulse.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Pulse.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, bool animate)
    {
        if (!animate) { target.BeginAnimation(property, null); ((DependencyObject)target).SetValue(property, to); return; }
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(Move.TimeSpan), Ease));
        target.BeginAnimation(property, animation);
    }
}

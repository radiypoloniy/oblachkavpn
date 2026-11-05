using System.Windows;
using System.Windows.Controls;

namespace Oblachka.Views;

public partial class QuickRouting : UserControl
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(QuickRouting));
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    public QuickRouting()
    {
        InitializeComponent();
        SizeChanged += (_, args) => Compact = args.NewSize.Width < 440;
    }
}

public sealed record QuickRouteTarget(string Name, AppRow? Application = null)
{
    public string Kind => Application is null ? "Сайт или адрес" : "Программа";
    public string EditLabel => "Настроить маршрут для " + Name;
    public Visibility SiteIcon => Application is null ? Visibility.Visible : Visibility.Collapsed;
}

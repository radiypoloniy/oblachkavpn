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
        AddButton.Click += (_, _) => ShowAdd(AddButton);
        SubscriptionsButton.Click += (_, _) => ShowAdd(SubscriptionsButton);
        // Поле ввода — сразу в фокусе: можно вставить ссылку, не целясь мышью.
        // Всплывающие окна не переживают уход со страницы и не лежат друг на друге.
        IsVisibleChanged += (_, _) => { if (!IsVisible) AddPopup.IsOpen = SortPopup.IsOpen = false; };
        SortPopup.Opened += (_, _) => AddPopup.IsOpen = false;
        AddPopup.Opened += (_, _) => SortPopup.IsOpen = false;
        AddPopup.Opened += (_, _) => Dispatcher.BeginInvoke(() => Add.KeyInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        RenderTransform = new TranslateTransform();
        SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 600;
            var host = narrow ? NarrowSearchSlot : HeaderSearchSlot;
            if (SearchField.Parent is Panel from && from != host) { from.Children.Remove(SearchField); host.Children.Add(SearchField); }
            NarrowSearchSlot.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    // Ключ в шапке — окно под ним справа; «Добавить» под списком — над строкой, без прокрутки.
    public void ShowAdd(FrameworkElement anchor)
    {
        var header = anchor == SubscriptionsButton;
        AddPopup.PlacementTarget = anchor;
        AddPopup.Placement = header ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top;
        AddPopup.HorizontalOffset = header ? anchor.ActualWidth - 384 + 12 : -12;
        AddPopup.IsOpen = true;
    }

    // Выезжает поверх главного окна: сдвиг 24 → 0 и проявление за 240 мс.
    public void Open()
    {
        AddPopup.IsOpen = SortPopup.IsOpen = false;
        Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ((TranslateTransform)RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }
}

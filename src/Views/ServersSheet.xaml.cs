using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Oblachka.Views;

public partial class ServersSheet : UserControl
{
    private bool refreshAll;

    public ServersSheet()
    {
        InitializeComponent();
        GroupTabsScroll.PreviewMouseWheel += (_, args) =>
        {
            if (GroupTabsScroll.ScrollableWidth <= 0 || args.Delta == 0) return;
            GroupTabsScroll.ScrollToHorizontalOffset(GroupTabsScroll.HorizontalOffset - args.Delta * 0.6);
            args.Handled = true;
        };
        GroupTabs.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (GroupTabs.ItemContainerGenerator.ContainerFromIndex(GroupTabs.SelectedIndex) is FrameworkElement tab)
                tab.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
        SubscriptionsButton.Click += (_, _) => ShowAdd(SubscriptionsButton);
        // Поле ввода — сразу в фокусе: можно вставить ссылку, не целясь мышью.
        // Всплывающие окна не переживают уход со страницы и не лежат друг на друге.
        IsVisibleChanged += (_, _) => { if (!IsVisible) AddPopup.IsOpen = SortPopup.IsOpen = RowMenuPopup.IsOpen = false; };
        SortPopup.Opened += (_, _) => AddPopup.IsOpen = RowMenuPopup.IsOpen = false;
        AddPopup.Opened += (_, _) => SortPopup.IsOpen = RowMenuPopup.IsOpen = false;
        RowMenuPopup.Opened += (_, _) => AddPopup.IsOpen = SortPopup.IsOpen = false;
        AddPopup.Opened += (_, _) => Dispatcher.BeginInvoke(() => Add.KeyInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        RenderTransform = new TranslateTransform();
        SizeChanged += (_, args) =>
        {
            // Строкам (ServerSheetRow.xaml): в узком окне плашка вариантов — только число.
            var compact = args.NewSize.Width < 480;
            SetRefreshScope(refreshAll);
            SheetList.Tag = compact ? "narrow" : null;
            var sortHost = compact ? HeaderTools : GroupTools;
            if (SortButton.Parent is Panel oldSortHost && oldSortHost != sortHost)
            {
                oldSortHost.Children.Remove(SortButton);
                sortHost.Children.Insert(compact ? 2 : 0, SortButton);
            }
            SortText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            AutoRecoverLabel.Visibility = AutoNote.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        };
    }

    internal void SetRefreshScope(bool all)
    {
        refreshAll = all;
        var label = all ? "Обновить все подписки" : "Обновить список подписки";
        RefreshButton.Content = all ? "Обновить все" : ActualWidth is > 0 and < 480 ? "Обновить" : "Обновить список";
        RefreshButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(RefreshButton, label);
    }

    // «+» в шапке — окно под ним справа.
    public void ShowAdd(FrameworkElement anchor)
    {
        AddPopup.PlacementTarget = anchor;
        AddPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        AddPopup.HorizontalOffset = anchor.ActualWidth - 384 + 12;
        AddPopup.IsOpen = true;
    }

    // Выезжает поверх главного окна: сдвиг 24 → 0 и проявление за 240 мс.
    public void Open()
    {
        AddPopup.IsOpen = SortPopup.IsOpen = RowMenuPopup.IsOpen = false;
        Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ((TranslateTransform)RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }
}

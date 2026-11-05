using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Oblachka.Views;

public partial class RulesTab : UserControl
{
    internal TextBox RuleDomain => Quick.Query;
    private readonly CollectionContainer ordinary = new(), pinned = new();
    private ServiceRouteRow[] pinnedRows = [];
    private bool showPinned = true;

    public RulesTab()
    {
        InitializeComponent();
        RuleList.ItemsSource = new CompositeCollection { ordinary, pinned };
        SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 560;
            ImportText.Visibility = ExportText.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            var serverSlot = narrow ? CompactServerSlot : InlineServerSlot;
            if (RuleSetServers.Parent is Panel previous && previous != serverSlot)
            {
                previous.Children.Remove(RuleSetServers);
                serverSlot.Children.Add(RuleSetServers);
            }
            CompactServerSlot.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            RuleSetText.MaxWidth = narrow ? 120 : 180;
            RuleSetServers.Margin = narrow ? new Thickness(0, 4, 0, 0) : new Thickness(6, 0, 0, 0);
            ImportRules.Padding = ExportRules.Padding = narrow ? new Thickness(10, 0, 4, 0) : new Thickness(12, 0, 14, 0);
        };
        RefreshEmpty();
    }

    internal void SetRules(RuleRow[] rows)
    {
        ordinary.Collection = rows;
        RefreshEmpty();
    }

    internal void SetPinnedRules(ServiceRouteRow[] rows)
    {
        pinnedRows = rows;
        if (showPinned) pinned.Collection = rows;
        RefreshEmpty();
    }

    internal void ShowPinnedRules(bool show)
    {
        if (showPinned == show) return;
        showPinned = show;
        pinned.Collection = show ? pinnedRows : null;
        RefreshEmpty();
    }

    internal void ShowTargetEditor(string title, bool templates)
    {
        EditorTitle.Text = title;
        ServicePresetPicker.Visibility = templates ? Visibility.Visible : Visibility.Collapsed;
        TargetEditor.Visibility = Visibility.Visible;
        TargetEditor.BringIntoView();
        (templates ? ServicePresetPicker : ServiceAction).Focus();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (TargetEditor.IsVisible) TargetEditor.BringIntoView();
        });
    }

    internal void CloseTargetEditor() => TargetEditor.Visibility = Visibility.Collapsed;

    private void RefreshEmpty()
    {
        var hasOrdinary = ordinary.Collection is RuleRow[] { Length: > 0 };
        var hasPinned = pinned.Collection is ServiceRouteRow[] { Length: > 0 };
        EmptyRules.Visibility = hasOrdinary || hasPinned ? Visibility.Collapsed : Visibility.Visible;
        RuleHint.Visibility = hasOrdinary ? Visibility.Visible : Visibility.Collapsed;
    }
}

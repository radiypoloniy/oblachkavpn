using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RoutePilot;

public partial class MainWindow
{
    private async Task CheckCompactServerLayoutAsync(List<string> checks)
    {
        var savedProfiles = importedProfiles.ToArray();
        var savedWidth = Width; var savedHeight = Height;
        try
        {
            Width = 960; Height = 660;
            LoadServerPreview(); Navigate("Servers");
            DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, "preview-servers-compact.png"));
            var viewer = Descendants<ScrollViewer>(ProfilePicker).First();
            var scrollbar = Descendants<ScrollBar>(viewer).First(s => s.Orientation == Orientation.Vertical);
            if (scrollbar.ActualWidth > 10.5 || scrollbar.Template.FindName("PART_Track", scrollbar) is not Track track ||
                track.Thumb is null || track.DecreaseRepeatButton is null || track.IncreaseRepeatButton is null)
                throw new InvalidOperationException("Compact scrollbar has incorrect width or lacks functional track parts");
            var firstRow = (ListBoxItem)ProfilePicker.ItemContainerGenerator.ContainerFromIndex(0);
            if (firstRow.ActualHeight > 48 || firstRow.ActualHeight < 40)
                throw new InvalidOperationException("Server rows have excessive height or insufficient click area");
            viewer.ScrollToEnd();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            ProfilePicker.UpdateLayout();
            if (viewer.VerticalOffset <= 0) throw new InvalidOperationException("Custom scrollbar cannot scroll the list");
            ProfilePicker.SelectedIndex = 23; ProfilePicker.ScrollIntoView(ProfilePicker.SelectedItem);
            ProfilePicker.UpdateLayout();
            var lastRow = ProfilePicker.ItemContainerGenerator.ContainerFromIndex(23) as ListBoxItem;
            if (lastRow is null || profile?.DisplayName != "Резервный сервер 18" || !lastRow.IsSelected)
                throw new InvalidOperationException("End-of-list selection was lost in compact layout");
            if (Servers.UseServerButton.ActualWidth < 120 || Servers.UseServerButton.ActualHeight < 32)
                throw new InvalidOperationException("Connection action is clipped in compact layout");
            checks.Add("960×660 server layout uses compact rows and a thin functional scrollbar; end-of-list selection remains available");
        }
        finally
        {
            Width = savedWidth; Height = savedHeight;
            ApplyImport(new ImportResult(savedProfiles, 0, 0)); Navigate("Connection");
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}

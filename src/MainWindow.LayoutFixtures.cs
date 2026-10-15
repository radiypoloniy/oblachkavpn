using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Oblachka;

public partial class MainWindow
{
    // «Все серверы» в окне 460×760: длинный список прокручивается, строки по 54 px,
    // кнопки внизу не обрезаны, выбор последнего сервера доступен.
    private async Task CheckCompactServerLayoutAsync(List<string> checks)
    {
        var savedProfiles = importedProfiles.ToArray();
        try
        {
            serviceProfiles = Array.Empty<Profile>(); externalProfiles = Array.Empty<Profile>(); profile = null;
            LoadServerPreview(); Navigate("Servers");
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, "preview-servers-compact.png"));
            var viewer = Descendants<ScrollViewer>(Servers).First(v => v.Content is StackPanel);
            var rows = Descendants<Border>(Servers.SheetList).Where(b => b.Name == "Plate" && b.TemplatedParent is ContentPresenter).ToArray();
            if (rows.Length < 24 || rows.Any(r => r.ActualHeight != 54))
                throw new InvalidOperationException("Server rows are missing or not 54 px high: " + rows.Length + " / " + string.Join(",", rows.Select(r => r.ActualHeight).Distinct()));
            viewer.ScrollToEnd();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (viewer.VerticalOffset <= 0) throw new InvalidOperationException("Server sheet cannot scroll the list");
            ProfilePicker.SelectedIndex = 23;
            if (profile?.DisplayName != "Резервный сервер 18") throw new InvalidOperationException("End-of-list selection was lost");
            if (Servers.MeasureButton.ActualWidth < 150 || Servers.BestButton.ActualWidth < 150 || Servers.BestButton.ActualHeight < 40)
                throw new InvalidOperationException("Sheet actions are clipped in the 460×760 window");
            checks.Add("460×760 server sheet scrolls 24 rows of 54 px; actions stay unclipped and end-of-list selection remains available");
        }
        finally
        {
            serviceProfiles = Array.Empty<Profile>(); externalProfiles = Array.Empty<Profile>(); profile = null;
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

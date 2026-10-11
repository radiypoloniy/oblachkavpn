using System.Windows;
using System.Windows.Controls;
using RoutePilot.Views;

namespace RoutePilot;

public partial class MainWindow
{
    private async Task CheckTrayOpenAsync(List<string> checks)
    {
        Hide();
        var closeCount = 0;
        var popup = new TrayPopup(() => { }, ShowWindow, () => { }) { ShowActivated = false };
        popup.Closing += (_, _) => popup.Dismiss(); // Same reentrancy as a Deactivated callback during Close.
        popup.Closed += (_, _) => closeCount++;
        try
        {
            popup.Show();
            popup.OpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            popup.Dismiss(); popup.Dismiss();
            if (closeCount != 1 || !IsVisible || closed)
                throw new InvalidOperationException("Tray Open fails under nested close/deactivation or closes the main window");
            checks.Add("Tray Open shows the hidden main window; repeated close/deactivation dismisses the popup only once");
        }
        finally { popup.Dismiss(); Hide(); }
    }
}

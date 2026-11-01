namespace Oblachka;

public partial class MainWindow
{
    private void FeedbackChanged(object? sender, EventArgs args) => ShowToast();
    private void NetworkAddressChanged(object? sender, EventArgs args)
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            networkSettle.Stop(); networkSettle.Start();
        });
    }

    private void ReleaseViewResources()
    {
        ThemeManager.Changed -= RefreshHome;
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.TextBlock.TextProperty,
            typeof(System.Windows.Controls.TextBlock)).RemoveValueChanged(ShellFeedback, FeedbackChanged);
        tray.ContextMenuStrip?.Dispose();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= MeasurementAddressChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        updateTimer.Stop(); networkSettle.Stop(); toastTimer.Stop();
        liveTimer?.Stop(); switchTimer?.Stop();
        measureCancel?.Cancel(); pinging?.Cancel(); tuning?.Cancel(); siteCheckCancel?.Cancel();
        meter?.Dispose(); meter = null;
    }
}

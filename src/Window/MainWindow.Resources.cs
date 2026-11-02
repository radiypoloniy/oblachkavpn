namespace Oblachka;

public partial class MainWindow
{
    private readonly CancellationTokenSource appCatalogCancel = new();
    private void FeedbackChanged(object? sender, EventArgs args) => ShowToast();
    private void OnProtectionRepaired() => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        DetailText.Text = "Защиту от утечек кто-то снял — Облачка поставила её заново.";
        Notify(Notice.ProtectionRepaired, "Защита от утечек снова на месте", "Её снял Windows или другая программа, Облачка поставила заново. Мимо VPN ничего не ушло.", warning: true);
    });
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
        runtime.Persistence.Changed -= PersistenceChanged;
        runtime.Connections.Changed -= ConnectionChanged;
        runtime.Imports.Applied -= ImportApplied; runtime.Imports.Changed -= ImportsChanged;
        runtime.Pause.Changed -= PauseChanged;
        runtime.Pause.Resuming -= PauseResuming;
        runtime.Pause.Resumed -= PauseResumed;
        runtime.Pause.ResumeFailed -= PauseResumeFailed;
        runtime.Proxy.Changed -= ProxyChanged;
        runtime.Updates.Changed -= UpdatesChanged;
        runtime.Updates.AvailableInBackground -= BackgroundUpdateAvailable;
        runtime.TunnelChanged -= OnSystemTunnelChanged;
        runtime.ProtectionRepaired -= OnProtectionRepaired;
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.TextBlock.TextProperty,
            typeof(System.Windows.Controls.TextBlock)).RemoveValueChanged(ShellFeedback, FeedbackChanged);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= MeasurementAddressChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        networkSettle.Stop(); toastTimer.Stop();
        liveTimer?.Stop(); switchTimer?.Stop();
        measureCancel?.Cancel(); pinging?.Cancel(); tuning?.Cancel(); siteCheckCancel?.Cancel();
        meter?.Dispose(); meter = null;
        appCatalogCancel.Cancel(); appCatalogCancel.Dispose();
    }
}

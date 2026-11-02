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
    private void ReleaseViewResources()
    {
        ThemeManager.Changed -= RefreshHome;
        runtime.Persistence.Changed -= PersistenceChanged;
        runtime.Connections.Changed -= ConnectionChanged;
        runtime.Measurements.Changed -= MeasurementsChanged;
        runtime.Rules.Changed -= RulesChanged; runtime.Networks.Changed -= NetworksChanged;
        runtime.Networks.Securing -= NetworkSecuring; runtime.Networks.Connected -= NetworkConnected;
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
        toastTimer.Stop();
        liveTimer?.Stop(); switchTimer?.Stop();
        tuning?.Cancel(); siteCheckCancel?.Cancel();
        appCatalogCancel.Cancel(); appCatalogCancel.Dispose();
    }
}

namespace Oblachka;

// Optional per-user Windows proxy pointing at the running core's local port (SOCKS5 + HTTP on one port).
public partial class MainWindow
{
    private int systemProxyPort => runtime.Proxy.Port;
    private void InitSystemProxy(bool enabled)
    {
        Settings.SystemProxy.Click += (_, _) => { QueueStateSave(); UpdateModeText(); SyncSystemProxy(engine.State); };
        runtime.Proxy.Changed += ProxyChanged;
        UpdateModeText();
        if (!enabled) { Settings.SystemProxy.IsEnabled = false; return; }
        runtime.Proxy.Start(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
    }
    private void SyncSystemProxy(ConnectionState state) => runtime.Proxy.Sync();
    private void ProxyChanged(ProxyChange change)
    {
        if (closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            DetailText.Text = change.Message;
        });
    }

    // Режим в подвале главного экрана и отметки в его меню.
    private void UpdateModeText()
    {
        var tunnel = SystemTunnelWanted;
        var proxy = !tunnel && preferences.SystemProxy;
        Home.ModeText.Text = tunnel ? "Весь компьютер" : proxy ? "Браузеры и программы" : "Только SOCKS";
        MarkRadio(ModeTunRadio, tunnel);
        MarkRadio(ModeProxyRadio, proxy);
        // Тот же выбор — в шапке «Программ и сайтов».
        MarkSegment(Live.ModeTun, tunnel); MarkSegment(Live.ModeProxy, proxy);
        ModeTunNote.Text = NetworkHelperConnection.ServiceRunning
            ? "Системный VPN: игры и любые программы."
            : "Системный VPN: игры и любые программы. Windows спросит права администратора.";
        RefreshSettings();
    }
}

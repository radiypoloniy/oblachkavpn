using System.Windows;

namespace Oblachka;

// Системный режим: весь трафик компьютера через туннель.
//
// Привилегированную часть выполняет отдельный процесс, запускаемый по запросу с явным
// повышением прав. Окно прав администратора не требует и ядром в этом режиме не управляет:
// порядок запуска и остановки держит SystemTunnelSession.
public partial class MainWindow
{
    private SystemTunnelSession? systemTunnel;
    private HelperProcessLifetime? helperLifetime;

    private bool SystemTunnelWanted => Settings.SystemTunnel.IsChecked == true;
    private bool SystemTunnelActive => systemTunnel is not null &&
        systemTunnel.State is not (SystemTunnelState.Idle or SystemTunnelState.Failed);

    private void InitSystemTunnel(bool enabled)
    {
        Settings.SystemTunnel.Click += SystemTunnelToggled;
        if (!enabled) { Settings.SystemTunnel.IsEnabled = false; return; }
        UpdateSystemTunnelNote(null);
    }

    private void SystemTunnelToggled(object sender, RoutedEventArgs args)
    {
        // Два системных режима сразу не имеют смысла: прокси указывал бы на порт,
        // который уже забрал туннель.
        if (SystemTunnelWanted && Settings.SystemProxy.IsChecked == true)
        {
            Settings.SystemProxy.IsChecked = false;
            SyncSystemProxy(engine.State);
            UpdateModeText();
        }
        if (!SystemTunnelWanted && SystemTunnelActive) _ = StopSystemTunnelAsync();
        UpdateModeText();
        UpdateSystemTunnelNote(systemTunnel?.State);
        UpdateControls();
        QueueStateSave();
    }

    private async Task StartSystemTunnelAsync(CancellationToken token)
    {
        if (profile is null) throw new UserError("Сначала выберите сервер.");
        DetailText.Text = "Готовим системный режим. Подтвердите запрос прав администратора.";
        var plan = await TunnelEndpoints.PrepareAsync(profile, token).ConfigureAwait(true);
        helperLifetime = await HelperProcessLifetime.ConnectAsync(NetworkHelperLauncher.Launch, token).ConfigureAwait(true);
        var session = new SystemTunnelSession(engine, helperLifetime.Client);
        session.Changed += OnSystemTunnelChanged;
        systemTunnel = session;
        await session.StartAsync(plan, token).ConfigureAwait(true);
    }

    private async Task StopSystemTunnelAsync()
    {
        var session = systemTunnel;
        var lifetime = helperLifetime;
        systemTunnel = null;
        helperLifetime = null;
        if (session is null) return;
        try { await session.StopAsync().ConfigureAwait(true); }
        catch (Exception ex) when (ex is UserError or IOException or TimeoutException)
        { if (!closed) DetailText.Text = "Системный режим остановлен не полностью. Проверьте сетевые настройки."; }
        finally
        {
            session.Changed -= OnSystemTunnelChanged;
            if (lifetime is not null) await lifetime.DisposeAsync().ConfigureAwait(true);
        }
    }

    private void OnSystemTunnelChanged(SystemTunnelState state)
    {
        if (closed) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closed) return;
            UpdateSystemTunnelNote(state);
            UpdateControls();
        }));
    }

    private void UpdateSystemTunnelNote(SystemTunnelState? state)
    {
        // Про блокировку говорится только то, что действительно сделано: решает это
        // помощник, а не окно. При выключенном брандмауэре Windows блокировать нечем.
        var running = systemTunnel?.BlocksBypass == true
            ? "Системный режим работает: весь трафик компьютера идёт через туннель, мимо него трафик заблокирован."
            : "Системный режим работает: весь трафик компьютера идёт через туннель. Блокировка трафика мимо туннеля " +
              "не действует — включите брандмауэр Windows, иначе при обрыве программы пойдут напрямую.";
        Settings.SystemTunnelNote.Text = state switch
        {
            SystemTunnelState.Starting => "Системный режим запускается.",
            SystemTunnelState.Running => running,
            SystemTunnelState.Stopping => "Системный режим останавливается.",
            SystemTunnelState.RecoveryRequired =>
                "Системный режим остановлен не полностью. Перезапустите приложение; если интерфейс Obl- остался, удалите его вручную.",
            SystemTunnelState.Failed => "Системный режим прерван. Подключение остановлено.",
            // Граница, о которой обязано сказать пояснение, теперь другая: блокировка
            // сделана средствами брандмауэра Windows, и при выключенном брандмауэре её
            // не будет. Обещать её безусловно нельзя.
            _ => "Весь трафик компьютера пойдёт через туннель, а мимо туннеля будет заблокирован средствами брандмауэра Windows. При выключенном брандмауэре блокировки не будет."
        };
    }

    private async Task CloseSystemTunnelAsync()
    {
        if (systemTunnel is not null || helperLifetime is not null) await StopSystemTunnelAsync().ConfigureAwait(true);
    }
}

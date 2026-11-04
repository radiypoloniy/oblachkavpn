using System.Windows;

namespace Oblachka;

// Жизненный цикл подключения: запуск, остановка и выбор между локальным режимом
// и системным. Выделено из MainWindow.xaml.cs, когда файл перерос лимит размера.
public partial class MainWindow
{
    private async void ConnectClick(object sender, RoutedEventArgs args) => await ConnectFreshAsync();
    private async Task ConnectFreshAsync()
    {
        manualStop = false;
        runtime.Pause.Cancel(rememberDisconnected: false); stoppedOnNetwork = null;
        recoveryWait?.Cancel(); recovery.Reset(); health.Reset(); ResetSystemPath();
        await PickBestBeforeConnectAsync();
        await ConnectCoreAsync();
    }
    private void ConnectionChanged() { if (!closed) UpdateControls(); }
    private async Task ConnectCoreAsync()
    {
        if (vmTestActive || profile is null || operation is not null || runtime.Connections.IsStopping) return;
        var target = profile; var previous = systemTunnel;
        try
        {
            if (SystemTunnelWanted && !SystemTunnelActive)
            {
                foreignVpn = ForeignTunnel.Find();
                if (foreignVpn is { } foreign) throw new UserError(ForeignTunnel.Explain(foreign));
                DetailText.Text = "Готовим системный режим.";
            }
            await runtime.Connections.StartAsync(target);
            if (closed) return;
            if (SystemTunnelWanted)
            {
                RememberTunnelDirect(); _ = SyncDirectDnsAsync();
                DetailText.Text = ReferenceEquals(previous, systemTunnel)
                    ? "Подключено к " + target.DisplayName + ". Блокировка не снималась."
                    : "Системный режим запущен: весь трафик идёт через туннель.";
                if (!ReferenceEquals(previous, systemTunnel)) _ = WarnEarlyGamesAsync();
            }
            else DetailText.Text = "Подключение запущено. Проверьте туннель в диагностике.";
            Connected();
        }
        catch (OperationCanceledException) { if (!closed) DetailText.Text = "Запуск отменён или истёк срок ожидания. Можно попробовать снова."; }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        catch (IOException) { if (!closed) DetailText.Text = "Служба Облачки оборвала связь при запуске. Попробуйте подключиться ещё раз."; }
        finally
        {
            if (!closed) { UpdateControls(); if (engine.State == ConnectionState.Failed) ScheduleRecovery(); else QueueServerSwitch(); }
        }
    }
    private async Task StopOwnedConnectionAsync()
    {
        systemStopping = true; UpdateControls();
        StatusText.Text = "Отключаемся…"; DetailText.Text = "Отключаем VPN…";
        try { await runtime.Connections.StopAsync(); if (!closed) DetailText.Text = "Подключение остановлено."; }
        catch (Exception error) when (error is UserError or IOException or TimeoutException or ObjectDisposedException)
        { if (!closed) DetailText.Text = "Остановить подключение полностью не удалось. Проверьте сетевые настройки."; }
        finally { systemStopping = false; if (!closed) { UpdateSystemTunnelNote(systemTunnel?.State); UpdateControls(); } }
    }
    private void StopClick(object sender, RoutedEventArgs args)
    {
        runtime.Pause.Cancel();
        manualStop = true;
        stoppedOnNetwork = currentNetwork?.Key;
        RememberConnected(false);
        recoveryWait?.Cancel();
        ResetSystemPath();
        operation?.Cancel();
        if (runtime.Connections.IsActive) { _ = StopOwnedConnectionAsync(); return; }
        // Системный режим снимает туннель и только потом ядро: иначе маршруты остались бы
        // указывать на мёртвый локальный порт.
        if (systemTunnel is not null) { _ = StopSystemTunnelAsync(); StatusText.Text = "Отключаемся…"; DetailText.Text = "Отключаем VPN…"; return; }
        // During startup the async owner tears down the engine; do not race process disposal with it.
        if (operation is null || !engineOperation) engine.Stop();
        else if (engine.State is ConnectionState.Running or ConnectionState.Verified) DetailText.Text = "Отменяем проверку…";
        if (operation is null) DetailText.Text = "Подключение остановлено.";
    }
}

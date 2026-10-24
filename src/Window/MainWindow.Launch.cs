namespace Oblachka;

// Подключение при запуске. VPN, включённый при выключении компьютера, включается снова сам:
// человек его не выключал. Выключение тумблером снимает отметку, выход и выключение Windows — нет.
// Здесь же — подключение заново, когда «Весь компьютер» прервался не по воле человека.
public partial class MainWindow
{
    private bool wasConnected, healingTunnel;
    private readonly Queue<DateTimeOffset> heals = new();
    // Подбор zapret отложен до подключения (RestoreZapret).
    private bool zapretAfterConnect;

    private bool ConnectAtLaunchPlanned => Settings.ConnectOnLaunch.IsChecked == true && wasConnected && !isolatedPersistence && !vmTestActive;

    private void InitLaunch()
    {
        Settings.ConnectOnLaunch.Click += (_, _) => QueueStateSave();
        Settings.LocalNetwork.Click += (_, _) => QueueStateSave();
        Settings.QuicOverTcp.Click += (_, _) => QueueStateSave();
        Settings.CompatibleStack.Click += (_, _) => QueueStateSave();
    }

    private void RememberConnected(bool connected)
    {
        if (wasConnected == connected) return;
        wasConnected = connected;
        QueueStateSave();
    }

    // Подключение состоялось (из ConnectCoreAsync).
    private void Connected()
    {
        RememberConnected(true);
        if (!zapretAfterConnect) return;
        zapretAfterConnect = false;
        _ = ZapretModeChangedAsync();
    }

    // Канал со службой оборвался (система надолго замирала, служба перезапустилась): служба держит
    // сессию, как после аварии окна, а окно её потеряло и показывает «Не подключено» — интернет закрыт
    // молча. Снимаем брошенную сессию (это делает подключение) и подключаемся заново, до трёх раз за 10 минут.
    private async Task HealLostTunnelAsync()
    {
        if (healingTunnel || closed || manualStop || systemStopping || !NetworkHelperConnection.ServiceRunning) return;
        while (heals.Count > 0 && DateTimeOffset.UtcNow - heals.Peek() > TimeSpan.FromMinutes(10)) heals.Dequeue();
        if (heals.Count >= 3) return;
        heals.Enqueue(DateTimeOffset.UtcNow);
        healingTunnel = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (closed || manualStop || systemStopping) return;
            DetailText.Text = "Связь со службой прервалась — подключаемся заново.";
            await StopSystemTunnelAsync();
            if (!closed && !manualStop && ConnectButton.IsEnabled) await ConnectCoreAsync();
        }
        finally { healingTunnel = false; }
    }

    private async Task ConnectAtLaunchAsync()
    {
        if (!ConnectAtLaunchPlanned || profile is null) return;
        DetailText.Text = "Подключаемся: VPN был включён, когда Облачка закрылась.";
        // После входа в Windows сеть поднимается не сразу (Wi-Fi), а без неё не разрешить адреса
        // серверов и не выбрать лучший. Ждём адаптер со шлюзом до 30 с, потом пробуем всё равно.
        for (var i = 0; i < 60 && !closed && ZapretRunner.PhysicalInterface() is null; i++) await Task.Delay(500);
        if (closed || operation is not null || engine.ProcessId is not null || !ConnectButton.IsEnabled) return;
        await ConnectFreshAsync();
    }
}

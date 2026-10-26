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

    // Сервис может попросить подключаться при каждом запуске (subscription-autoconnect) — «Сразу подключаться»
    // по-прежнему решает человек: выключил её — не подключаемся.
    private string? ServiceAutoConnect => ServiceGroup?.Metadata?.Provider?.AutoConnect;
    private bool ConnectAtLaunchPlanned => Settings.ConnectOnLaunch.IsChecked == true && (wasConnected || ServiceAutoConnect is not null) && !isolatedPersistence && !vmTestActive &&
        CurrentNetworkMode != NetworkMode.Trusted;

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
        ReadNetwork();
        ApplyNetworkRuleSet();
        // Не подключаемся по «Сразу подключаться» — решает сеть («Всегда VPN», незнакомые Wi-Fi).
        if (!ConnectAtLaunchPlanned) { if (!isolatedPersistence && !vmTestActive) await ApplyNetworkAsync(); return; }
        if (profile is null) return;
        DetailText.Text = wasConnected ? "Подключаемся: VPN был включён, когда Облачка закрылась." : "Подключаемся: так настроил сервис.";
        // lowestdelay — сервис просит самый быстрый сервер: это «Автовыбор».
        if (ServiceAutoConnect == ProviderOptions.LowestDelay && !autoSelect) { autoSelect = true; QueueStateSave(); }
        // После входа в Windows сеть поднимается не сразу (Wi-Fi), а без неё не разрешить адреса
        // серверов и не выбрать лучший. Ждём адаптер со шлюзом до 30 с, потом пробуем всё равно.
        for (var i = 0; i < 60 && !closed && ZapretRunner.PhysicalInterface() is null; i++) await Task.Delay(500);
        if (closed || operation is not null || engine.ProcessId is not null || !ConnectButton.IsEnabled) return;
        await ConnectFreshAsync();
    }
}

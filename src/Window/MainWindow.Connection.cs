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
        pausedUntil = null; stoppedOnNetwork = null;
        recoveryWait?.Cancel(); recovery.Reset(); health.Reset(); ResetSystemPath();
        await PickBestBeforeConnectAsync();
        await ConnectCoreAsync();
    }
    private async Task ConnectCoreAsync()
    {
        if (vmTestActive || profile is null || operation is not null) return;
        // Что запускаем, фиксируется сразу: выбор в списке может смениться, пока идёт запуск,
        // и тогда переключение на новый выбор выполнится следом (QueueServerSwitch).
        var target = profile;
        manualStop = false;
        // Правила сайтов: в обычном режиме — у ядра подключения, в системном — у ядра туннеля.
        if (engine is Engine core)
        {
            core.SiteRules = SystemTunnelWanted ? Array.Empty<SiteRule>() : siteRules.ToArray();
            core.Fragment = FragmentWanted; core.FragmentShape = advanced.Fragment(ProviderFragment is { Enabled: true } shape ? shape : null);
            // Списки у ядра туннеля (TunnelLists) — ядру окна под той же сессией они не нужны.
            core.Routing = TunnelListsActive ? null : CurrentRouting();
            coreZapretKey = DesiredZapretKey; appliedZapretDomains = ZapretRouting ? zapretDomains : [];
            // Новый вход прямого трафика — только на новую сессию «Весь компьютер»; перезапуск ядра
            // под действующей сессией сохраняет её вход.
            if (!(SystemTunnelWanted && SystemTunnelActive))
                // С zapret вход нужен заранее: его сайты включаются в «напрямую» без переподключения туннеля.
                core.DirectEntry = SystemTunnelWanted && (core.Routing?.HasDirect == true || zapretEnabled) ? CoreDirectEntry.Create(Engine.FreePort()) : null;
        }
        operation = new CancellationTokenSource();
        engineOperation = true;
        UpdateControls();
        try
        {
            // В системном режиме порядок запуска держит SystemTunnelSession: сначала ядро,
            // затем туннель у привилегированного помощника. Окно само ядро не стартует.
            // Сессия уже держит туннель и блокировку: повторный запуск — это перезапуск
            // ядра под защитой, а не второй туннель и не второй запрос прав.
            if (SystemTunnelWanted && SystemTunnelActive && systemTunnel is { } session)
            {
                await session.SwitchAsync(profile, operation.Token);
                _ = SyncDirectDnsAsync();
                StartedFresh();
                runningProfile = target;
                if (!closed) DetailText.Text = "Подключено к " + profile.DisplayName + ". Блокировка не снималась.";
            }
            else if (SystemTunnelWanted)
            {
                await StartSystemTunnelAsync(operation.Token);
                sentDirectDns = ""; _ = SyncDirectDnsAsync();
                StartedFresh();
                runningProfile = target;
                if (!closed) DetailText.Text = "Системный режим запущен: весь трафик идёт через туннель.";
                _ = WarnEarlyGamesAsync();
            }
            else
            {
                await engine.StartAsync(profile, operation.Token);
                StartedFresh();
                runningProfile = target;
                if (!closed) DetailText.Text = "Подключение запущено. Проверьте туннель в диагностике.";
            }
            if (!closed) Connected();
        }
        catch (OperationCanceledException) { if (!closed) DetailText.Text = "Запуск отменён или истёк срок ожидания. Можно попробовать снова."; }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        // Служба оборвала канал посреди запуска (на стенде — первое подключение сразу после установки):
        // сессию уже свернул StartWithSparesAsync, окно не должно падать.
        catch (IOException) { if (!closed) DetailText.Text = "Служба Облачки оборвала связь при запуске. Попробуйте подключиться ещё раз."; }
        finally { if (manualStop) engine.Stop(); operation.Dispose(); operation = null; engineOperation = false; if (!closed) { UpdateControls(); if (engine.State == ConnectionState.Failed) ScheduleRecovery(); else QueueServerSwitch(); } }
    }
    private void StopClick(object sender, RoutedEventArgs args)
    {
        manualStop = true;
        stoppedOnNetwork = currentNetwork?.Key;
        RememberConnected(false);
        recoveryWait?.Cancel();
        ResetSystemPath();
        operation?.Cancel();
        // Системный режим снимает туннель и только потом ядро: иначе маршруты остались бы
        // указывать на мёртвый локальный порт.
        if (systemTunnel is not null) { _ = StopSystemTunnelAsync(); StatusText.Text = "Отключаемся…"; DetailText.Text = "Отключаем VPN…"; return; }
        // During startup the async owner tears down the engine; do not race process disposal with it.
        if (operation is null || !engineOperation) engine.Stop();
        else if (engine.State is ConnectionState.Running or ConnectionState.Verified) DetailText.Text = "Отменяем проверку…";
        if (operation is null) DetailText.Text = "Подключение остановлено.";
    }
}

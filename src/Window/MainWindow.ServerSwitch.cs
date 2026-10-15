using System.Windows.Threading;

namespace Oblachka;

// Смена сервера на ходу: выбор другого сервера в списке при работающем подключении сразу
// переключает на него, без «Остановить» → выбрать → «Подключиться».
//
// В системном режиме узлы сервиса разрешены защитой ещё при запуске, поэтому переключение
// идёт под защитой: перезапускается только ядро, туннель и блокировка остаются. Сторонний
// узел в этот набор не входит — для него сессия ставится заново, и защита на это время
// снимается; пользователь видит об этом пояснение.
//
// Выбор срабатывает с задержкой: при листании списка стрелками переподключение не должно
// идти на каждый промелькнувший сервер. Если в этот момент идёт фоновая проверка связи,
// выбор человека важнее: проверка отменяется, переключение ждёт её завершения.
public partial class MainWindow
{
    private Profile? runningProfile;
    private DispatcherTimer? switchTimer;
    // Фоновая проверка связи раз в 45 с не должна гасить список серверов.
    private bool backgroundCheck;
    // Замер серверов при подключении: «Остановить» отменяет замер, а не подключение.
    private bool measuring;

    private bool ConnectionActive => engine.State is not ConnectionState.Idle || SystemTunnelActive;
    private bool SwitchPending => profile is not null && runningProfile is not null &&
        Profiles.Fingerprint(profile) != Profiles.Fingerprint(runningProfile);

    private void QueueServerSwitch()
    {
        if (restoringState || closed || !ConnectionActive) return;
        // Подбор сам меняет выбор, это не просьба человека переключиться.
        if (findingRoute) return;
        switchTimer ??= CreateSwitchTimer();
        switchTimer.Stop();
        if (!SwitchPending) return;
        if (backgroundCheck) operation?.Cancel();
        switchTimer.Start();
    }

    private DispatcherTimer CreateSwitchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += async (_, _) =>
        {
            // Отменённая проверка ещё завершается: ждём следующего тика.
            if (operation is not null) return;
            timer.Stop();
            await SwitchServerAsync();
        };
        return timer;
    }

    private async Task SwitchServerAsync()
    {
        if (operation is not null || closed || profile is null || !ConnectionActive || !SwitchPending) return;
        recoveryWait?.Cancel(); recovery.Reset(); health.Reset(); manualStop = false;
        if (SystemTunnelActive && systemTunnel is { } session)
        {
            if (!session.CanSwitchTo(profile))
            {
                DetailText.Text = profile.IsService
                    ? "Этот сервер не вошёл в набор защиты при подключении: переподключаемся, на это время защита снимается."
                    : "Переподключаемся на сторонний сервер: на это время защита снимается.";
                await StopSystemTunnelAsync();
            }
            else DetailText.Text = "Переключаемся на " + profile.DisplayName + ". Блокировка не снимается.";
        }
        else
        {
            DetailText.Text = "Переключаемся на " + profile.DisplayName + ".";
            engine.Stop();
        }
        await ConnectCoreAsync();
    }
}

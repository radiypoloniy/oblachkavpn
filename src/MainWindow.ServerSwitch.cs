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
// идти на каждый промелькнувший сервер.
public partial class MainWindow
{
    private Profile? runningProfile;
    private DispatcherTimer? switchTimer;

    private bool ConnectionActive => engine.State is not ConnectionState.Idle || SystemTunnelActive;

    private void QueueServerSwitch()
    {
        // Программная смена выбора (подбор, восстановление) идёт внутри операции и сюда не попадает.
        if (operation is not null || restoringState || closed || profile is null || runningProfile is null || !ConnectionActive) return;
        switchTimer ??= CreateSwitchTimer();
        switchTimer.Stop();
        if (Profiles.Fingerprint(profile) == Profiles.Fingerprint(runningProfile)) return;
        switchTimer.Start();
    }

    private DispatcherTimer CreateSwitchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += async (_, _) => { timer.Stop(); await SwitchServerAsync(); };
        return timer;
    }

    private async Task SwitchServerAsync()
    {
        if (operation is not null || closed || profile is null || runningProfile is null || !ConnectionActive) return;
        if (Profiles.Fingerprint(profile) == Profiles.Fingerprint(runningProfile)) return;
        recoveryWait?.Cancel(); recovery.Reset(); health.Reset(); manualStop = false;
        if (SystemTunnelActive && systemTunnel is { } session)
        {
            if (!session.CanSwitchTo(profile))
            {
                DetailText.Text = "Переподключаемся на сторонний сервер: на это время защита снимается.";
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

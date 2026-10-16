namespace Oblachka;

// Проверка «как у программ» под «Весь компьютер» (SystemPathProbe). Ядро может отвечать через
// свой SOCKS, а трафик компьютера при этом не проходить — так было в 0.13.1, и окно молчало.
// Порядок: две неудачные проверки подряд → туннель пересобирается один раз → не помогло —
// карточка «Интернет закрыт» с кнопкой выключения и уведомление в трее, а не «Защищено».
public partial class MainWindow
{
    private readonly TunnelHealth pathHealth = new(), directHealth = new();
    private const int PathFailureLimit = 2;
    // pathRebuilt — туннель уже пересобран из-за этой поломки; pathBroken — не помогло, сеть закрыта.
    private bool pathRebuilt, pathBroken;
    private string pathProblem = "";
    // Сломан только прямой путь: сайты через VPN открываются, идущие напрямую — нет.
    private bool pathDirectOnly;

    // null — путь программ в порядке (или проверять нечего); иначе — что сломано. Вызывается
    // после удачной проверки ядра, внутри operation проверки связи.
    private async Task<string?> CheckSystemPathAsync(CancellationToken token)
    {
        if (!SystemTunnelActive) { pathHealth.Reset(); directHealth.Reset(); return null; }
        var tunnel = await pathHealth.CheckAsync(SystemPathProbe.RunAsync, TunnelHealth.Targets, token);
        pathDirectOnly = false;
        if (!tunnel.Reachable) return "Сервер отвечает, но программы компьютера через туннель в интернет не выходят.";
        // Прямой путь проверяем по сайтам, которые точно идут мимо VPN: «Российские сайты напрямую».
        if (Settings.RussiaDirect.IsChecked == true && (engine as Engine)?.Routing?.HasDirect == true)
        {
            var direct = await directHealth.CheckAsync(SystemPathProbe.RunAsync, SystemPathProbe.DirectTargets, token);
            if (!direct.Reachable) { pathDirectOnly = true; return "Сайты через VPN открываются, а российские, которые идут напрямую, — нет."; }
        }
        else directHealth.Reset();
        return null;
    }

    private bool SystemPathShouldRepair => pathHealth.FailedRounds >= PathFailureLimit || directHealth.FailedRounds >= PathFailureLimit;

    private void SystemPathHealthy()
    {
        pathRebuilt = false;
        if (!pathBroken) return;
        pathBroken = false; networkClosed = false;
        DetailText.Text = "Связь через туннель восстановилась.";
        RefreshHome();
    }

    private async Task RepairSystemPathAsync(string problem)
    {
        if (closed || operation is not null || profile is null || systemTunnel is null) return;
        pathProblem = problem;
        if (pathRebuilt)
        {
            var first = !pathBroken;
            pathBroken = true; networkClosed = true;
            DetailText.Text = problem + " Переподключение не помогло. «Выключить VPN и открыть интернет» сразу вернёт обычную сеть.";
            if (first) tray.ShowBalloonTip(8000, ServiceBrand.Name, "Интернет через VPN не работает. Откройте Облачку и нажмите «Выключить VPN и открыть интернет».",
                System.Windows.Forms.ToolTipIcon.Warning);
            // Проверка продолжается: если путь оживёт сам, карточка уйдёт.
            nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(30);
            RefreshHome();
            return;
        }
        pathRebuilt = true;
        DetailText.Text = problem + " Переподключаем «Весь компьютер»…";
        manualStop = true; recoveryWait?.Cancel();
        await StopSystemTunnelAsync();
        UpdateControls();
        pathHealth.Reset(); directHealth.Reset();
        if (!closed && ConnectButton.IsEnabled) await ConnectCoreAsync();
    }

    // Ручное подключение и остановка начинают с чистого листа.
    private void ResetSystemPath()
    {
        pathRebuilt = false; pathBroken = false;
        pathHealth.Reset(); directHealth.Reset();
    }
}

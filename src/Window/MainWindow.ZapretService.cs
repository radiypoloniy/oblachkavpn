using System.Windows;

namespace Oblachka;

// Связь окна с zapret в службе: команды, маршруты, восстановление при запуске, остановка при выходе.
public partial class MainWindow
{
    // С каким набором сайтов zapret ядро запущено на самом деле — его ставит ConnectCoreAsync,
    // когда собирает маршруты. Раньше флаг ставился при запросе: если ядро в ту секунду было
    // занято, перезапуск молча пропускался, и YouTube с Discord оставались «напрямую» без обхода.
    private string coreZapretKey = "";
    private bool zapretRoutingRetry;
    private DateTimeOffset zapretRoutingTried = DateTimeOffset.MinValue;

    // Какой набор нужен сейчас: домены открывшихся групп или ничего.
    private string DesiredZapretKey => ZapretRouting ? string.Join(',', zapretDomains) : "";
    // Набор, с которым ядро туннеля работает сейчас: его соединения закрываются при смене (CloseZapretConnectionsAsync).
    private string[] appliedZapretDomains = [];

    // Команда службе: через соединение «Весь компьютер», если оно со службой, иначе через временное.
    // Помощник с UAC zapret не запускает — только установленная служба.
    private async Task<ZapretStatus?> ZapretCallAsync(ZapretRequest request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            if (helperLifetime is { IsService: true } lifetime)
                return zapretStatus = await lifetime.Client.ZapretAsync(request, timeout.Token);
            if (!NetworkHelperConnection.ServiceRunning) return zapretStatus = null;
            await using var temporary = await ServiceHelperLifetime.ConnectAsync(NetworkServiceControl.Query().ProcessId, timeout.Token);
            return zapretStatus = await temporary.Client.ZapretAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is UserError or IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return zapretStatus = new(false, false, null, null, Array.Empty<string>(), "Служба не ответила на команду zapret.");
        }
    }

    // Сайты zapret включаются в маршруты (напрямую) или выключаются; подключение перезапускается,
    // только если это что-то меняет.
    private async Task ApplyZapretRoutingAsync()
    {
        RefreshZapret();
        if (!ConnectionActive || DesiredZapretKey == coreZapretKey) return;
        await RoutingChangedAsync();
        await CloseZapretConnectionsAsync();
    }

    // Списки в ядре туннеля меняются на ходу, а открытые соединения остаются на прежнем пути:
    // браузер держал YouTube напрямую (RU у логотипа), когда обход уже вернул его в VPN. Как с
    // правилами программ (CoreConnections.CloseChangedAsync), закрываем соединения к сайтам,
    // чей путь сменился, — браузер переподключится уже по новому.
    private async Task CloseZapretConnectionsAsync()
    {
        var now = ZapretRouting ? zapretDomains : [];
        if (coreZapretKey != DesiredZapretKey) return;
        var changed = appliedZapretDomains.Except(now).Concat(now.Except(appliedZapretDomains)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        appliedZapretDomains = now;
        if (changed.Count == 0 || !TunnelListsActive || systemTunnel?.Stats is not { } stats) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await CoreConnections.CloseHostsAsync(stats, host => ZapretSites.Covers(changed, host), timeout.Token);
    }

    // Догоняет расхождение, если перезапуск ядра не случился (оно было занято). Не чаще раза
    // в 10 с: если ядро не поднимается, это разбирает проверка связи, а не частые перезапуски.
    private void PollZapretRouting()
    {
        if (closed || zapretRoutingRetry || operation is not null || systemStopping || !ConnectionActive || DesiredZapretKey == coreZapretKey ||
            DateTimeOffset.UtcNow - zapretRoutingTried < TimeSpan.FromSeconds(10)) return;
        zapretRoutingRetry = true; zapretRoutingTried = DateTimeOffset.UtcNow;
        _ = Retry();
        async Task Retry() { try { await RoutingChangedAsync(); await CloseZapretConnectionsAsync(); } finally { zapretRoutingRetry = false; } }
    }

    private IEnumerable<RoutingProfile> ZapretRoutingProfiles()
    {
        if (ZapretRouting) yield return ZapretProfile;
    }

    // После запуска: включённый обход поднимается сам — запомненной для этой сети стратегией
    // или новым подбором, если сеть другая или стратегия перестала работать.
    private void RestoreZapret()
    {
        if (!zapretEnabled || isolatedPersistence || stateStore is null) return;
        // Подбор мерит прямой путь. Под сессией, оставленной прошлым запуском (сеть закрыта), и перед
        // автоподключением он провалил бы все проверки — тогда он идёт после подключения.
        if (ConnectAtLaunchPlanned || SessionLeft(NetworkService.JournalPath)) { zapretAfterConnect = true; return; }
        Dispatcher.BeginInvoke(async () => { if (!closed && NetworkHelperConnection.ServiceRunning) await PickZapretAsync(force: false); });
    }

    // Смена режима меняет путь сайтов: запомненная стратегия перепроверяется, когда
    // переключение закончилось. Подбор посреди снятия или подъёма туннеля мерил бы не то.
    private async Task ZapretModeChangedAsync()
    {
        if (!zapretEnabled) return;
        zapretCancel?.Cancel();
        for (var i = 0; i < 120 && !closed && (zapretBusy || systemStopping || operation is not null); i++) await Task.Delay(250);
        if (!closed) await PickZapretAsync(force: false);
    }

    // Выход из приложения выключает обход: «работает, пока запущена Облачка».
    private void StopZapretOnExit()
    {
        if (!zapretEnabled || zapretStatus?.Running != true) return;
        try { Task.Run(() => ZapretStopDirectAsync()).Wait(TimeSpan.FromSeconds(4)); }
        catch (AggregateException) { }
    }

    private static async Task ZapretStopDirectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await using var temporary = await ServiceHelperLifetime.ConnectAsync(NetworkServiceControl.Query().ProcessId, timeout.Token).ConfigureAwait(false);
        await temporary.Client.ZapretAsync(new(ZapretAction.Stop), timeout.Token).ConfigureAwait(false);
    }
}

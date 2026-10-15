using System.Windows;

namespace Oblachka;

// Связь окна с zapret в службе: команды, маршруты, восстановление при запуске, остановка при выходе.
public partial class MainWindow
{
    private bool zapretRoutingApplied;

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
        var wanted = ZapretRouting;
        if (wanted == zapretRoutingApplied) { RefreshZapret(); return; }
        zapretRoutingApplied = wanted;
        RefreshZapret();
        await RoutingChangedAsync();
    }

    private IEnumerable<RoutingProfile> ZapretRoutingProfiles()
    {
        if (zapretRoutingApplied && zapretDomains.Length > 0) yield return ZapretProfile;
    }

    // После запуска: включённый обход поднимается сам — запомненной для этой сети стратегией
    // или новым подбором, если сеть другая или стратегия перестала работать.
    private void RestoreZapret()
    {
        if (!zapretEnabled || isolatedPersistence || stateStore is null) return;
        Dispatcher.BeginInvoke(async () => { if (!closed && NetworkHelperConnection.ServiceRunning) await PickZapretAsync(force: false); });
    }

    // Смена режима: в «Весь компьютер» winws гасится до подключения (его гасит и служба, ставя
    // защиту), в «Браузеры и программы» обход поднимается снова.
    private async Task ZapretModeChangedAsync()
    {
        if (!zapretEnabled) return;
        zapretCancel?.Cancel();
        if (!zapretBusy) await PickZapretAsync(force: false);
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

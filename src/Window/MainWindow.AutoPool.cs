using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Пул «Автовыбора» (AutoPool) и замер задержки через серверы (ServerMeasurements).
public partial class MainWindow
{
    private AutoPool autoPool => runtime.Profiles.AutoPool;
    private CancellationTokenSource? measureCancel;
    private DateTimeOffset lastMeasure = DateTimeOffset.MinValue, lastThroughput = DateTimeOffset.MinValue;
    // В какой сети мерили скорость (NetworkIdentity): в новой прежние цифры не годятся.
    private string? throughputNetwork;

    private static ITunnelController MeasureEngine() => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "");
    // Ядро для замера по MeasureRoute: под «Весь компьютер» оно выходит к серверу через физический
    // интерфейс (PhysicalBinding), иначе мерило бы сервер через туннель.
    private ITunnelController RouteEngine() => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "") { Physical = systemTunnel is not null };

    // Чем замерять сервер: под «Весь компьютер» — его профилем с адресом, который защита
    // разрешила ядрам (иначе отдельное ядро упрётся в защиту и сервер «не ответит» за секунду);
    // null — сервера среди разрешённых нет, сейчас его не проверить.
    private Profile? MeasureRoute(Profile item) => systemTunnel is { } session ? session.PinnedFor(item) : item;

    // quiet — фоновый замер пула: без надписей, не мешает подключению.
    private async Task MeasureServersAsync(IReadOnlyList<Profile> targets, bool quiet)
    {
        if (measuring || closed || targets.Count == 0) return;
        measuring = true; lastMeasure = DateTimeOffset.UtcNow;
        // Скорость — по кнопке, а в фоне раз в 3 часа и сразу в новой сети: до 4 МБ у трёх лучших
        // по задержке и у текущего (ServerMeasurements.ThroughputTargets).
        // В лимитной сети (NetworkCost) фоном только задержка — она стоит единицы килобайт.
        var network = NetworkIdentity.Current();
        var throughput = !quiet || (network != throughputNetwork || DateTimeOffset.UtcNow - lastThroughput > TimeSpan.FromHours(3)) && !NetworkCost.Metered();
        if (throughput) { lastThroughput = DateTimeOffset.UtcNow; throughputNetwork = network; }
        measureCancel = new CancellationTokenSource();
        Servers.MeasureButton.ToolTip = "Остановить замер"; System.Windows.Automation.AutomationProperties.SetName(Servers.MeasureButton, "Остановить замер");
        var skipped = targets.Count(t => MeasureRoute(t) is null);
        var done = 0;
        try
        {
            void Measured(ServerMeasurement result, string progress)
            {
                // Замер без скорости не стирает прежнюю: она меряется реже задержки.
                if (result is { Success: true, Throughput: 0 } && measurements.TryGetValue(result.Fingerprint, out var old) && old.Throughput > 0)
                    result = result with { Throughput = old.Throughput };
                measurements[result.Fingerprint] = result;
                if (closed) return;
                if (!quiet) DetailText.Text = progress + " Можно остановить.";
                PingsChanged();
            }
            await ServerMeasurements.MeasureAsync(targets, RouteEngine, result => Measured(result, $"Пинг: {++done} из {targets.Count - skipped}."), measureCancel.Token, MeasureRoute);
            if (throughput)
            {
                var fastest = ServerMeasurements.ThroughputTargets(targets, measurements, DateTimeOffset.UtcNow, runningProfile ?? profile);
                var speedDone = 0;
                await ServerMeasurements.MeasureAsync(fastest, RouteEngine, result => Measured(result, $"Скорость лучших: {++speedDone} из {fastest.Count}."), measureCancel.Token, MeasureRoute, throughput: true);
            }
            QueueStateSave();
            if (!quiet && !closed) DetailText.Text = "Пинг обновлён: задержка через каждый сервер." +
                (skipped > 0 ? $" {skipped} не проверить, пока включён «Весь компьютер»: их адреса закрыты защитой." : "");
        }
        catch (OperationCanceledException) { QueueStateSave(); if (!quiet && !closed) DetailText.Text = "Замер остановлен. Полученные результаты сохранены."; }
        finally
        {
            measuring = false; measureCancel.Dispose(); measureCancel = null;
            TrimMeasurements();
            if (!closed) { Servers.MeasureButton.ToolTip = "Обновить пинг"; System.Windows.Automation.AutomationProperties.SetName(Servers.MeasureButton, "Обновить пинг"); }
        }
    }

    // Набор правил с серверами (MainWindow.RuleSetServers) сужает и пул: автовыбор и подбор — только через них.
    private bool InAutoPool(Profile item) => runtime.Profiles.InAutoPool(item);

    // «Настройки → Автовыбор»: строка на сервер с переключателем.
    private void RenderAutoPool()
    {
        if (!viewsReady) return;
        var list = Settings.AutoPoolList;
        RefreshSettingsSummary();
        // Бесплатные чужие узлы в пул не входят никогда — и в списке пула их нет.
        list.ItemsSource = importedProfiles.Where(p => !p.IsFree).Select(p => new Views.AutoPoolRow(p, autoPool.Contains(p))).ToArray();
    }
}

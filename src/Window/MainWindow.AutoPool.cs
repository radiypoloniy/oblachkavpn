using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Пул «Автовыбора» (AutoPool) и замер задержки через серверы (ServerMeasurements).
public partial class MainWindow
{
    private readonly AutoPool autoPool = new();
    private CancellationTokenSource? measureCancel;
    private DateTimeOffset lastMeasure = DateTimeOffset.MinValue, lastThroughput = DateTimeOffset.MinValue;
    // В какой сети мерили скорость (NetworkIdentity): в новой прежние цифры не годятся.
    private string? throughputNetwork;

    private static ITunnelController MeasureEngine() => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "");

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
        Servers.MeasureButton.Content = "Остановить замер";
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
                RenderSheet(); RefreshHome();
            }
            await ServerMeasurements.MeasureAsync(targets, MeasureEngine, result => Measured(result, $"Пинг: {++done} из {targets.Count - skipped}."), measureCancel.Token, MeasureRoute);
            if (throughput)
            {
                var fastest = ServerMeasurements.ThroughputTargets(targets, measurements, DateTimeOffset.UtcNow, runningProfile ?? profile);
                var speedDone = 0;
                await ServerMeasurements.MeasureAsync(fastest, MeasureEngine, result => Measured(result, $"Скорость лучших: {++speedDone} из {fastest.Count}."), measureCancel.Token, MeasureRoute, throughput: true);
            }
            QueueStateSave();
            if (!quiet && !closed) DetailText.Text = "Пинг обновлён: задержка через каждый сервер." +
                (skipped > 0 ? $" {skipped} не проверить, пока включён «Весь компьютер»: их адреса закрыты защитой." : "");
        }
        catch (OperationCanceledException) { QueueStateSave(); if (!quiet && !closed) DetailText.Text = "Замер остановлен. Полученные результаты сохранены."; }
        finally
        {
            measuring = false; measureCancel.Dispose(); measureCancel = null;
            if (!closed) Servers.MeasureButton.Content = "Обновить пинг";
        }
    }

    private bool InAutoPool(Profile item) => autoPool.Contains(item);

    // «Настройки → Автовыбор»: строка на сервер с переключателем.
    private void RenderAutoPool()
    {
        if (!viewsReady) return;
        var list = Settings.AutoPoolList;
        list.Children.Clear();
        RefreshSettingsSummary();
        // Бесплатные чужие узлы в пул не входят никогда — и в списке пула их нет.
        foreach (var item in importedProfiles.Where(p => !p.IsFree))
        {
            var toggle = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = autoPool.Contains(item), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(toggle, "«" + item.DisplayName + "» в автовыборе");
            toggle.Click += (_, _) =>
            {
                autoPool.Set(item, toggle.IsChecked == true);
                QueueStateSave(); RenderSheet(); RefreshHome(); RefreshSettingsSummary();
            };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.DisplayName, FontSize = Tokens.Size("FsRow"), TextTrimming = TextTrimming.CharacterEllipsis });
            var sub = AutoPool.IsWhitelistBypass(item) ? "обход белых списков" : !item.IsService ? "сторонняя подписка" : null;
            if (sub is not null) text.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Small") });
            var row = new DockPanel { Margin = new Thickness(12, 7, 6, 7) };
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle); row.Children.Add(text);
            list.Children.Add(row);
        }
    }
}

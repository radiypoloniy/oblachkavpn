using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Пул «Автовыбора» (AutoPool) и замер задержки через серверы (ServerMeasurements).
public partial class MainWindow
{
    private readonly AutoPool autoPool = new();
    private CancellationTokenSource? measureCancel;
    private DateTimeOffset lastMeasure = DateTimeOffset.MinValue;

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
        measureCancel = new CancellationTokenSource();
        Servers.MeasureButton.Content = "Остановить замер";
        var skipped = targets.Count(t => MeasureRoute(t) is null);
        var done = 0;
        try
        {
            await ServerMeasurements.MeasureAsync(targets, MeasureEngine, result =>
            {
                measurements[result.Fingerprint] = result;
                if (closed) return;
                if (!quiet) DetailText.Text = $"Пинг: {++done} из {targets.Count - skipped}. Можно остановить.";
                RenderSheet(); RefreshHome();
            }, measureCancel.Token, MeasureRoute);
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

    // «Настройки → Основное → Автовыбор»: строка на сервер с переключателем.
    private void RenderAutoPool()
    {
        if (!viewsReady) return;
        var list = Settings.AutoPoolList;
        list.Children.Clear();
        foreach (var item in importedProfiles)
        {
            var toggle = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = autoPool.Contains(item), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(toggle, "«" + item.DisplayName + "» в автовыборе");
            toggle.Click += (_, _) =>
            {
                autoPool.Set(item, toggle.IsChecked == true);
                QueueStateSave(); RenderSheet(); RefreshHome();
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

using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Пул «Автовыбора» (AutoPool) и замер задержки через серверы (ServerMeasurements).
public partial class MainWindow
{
    private AutoPool autoPool => runtime.Profiles.AutoPool;
    private static ITunnelController MeasureEngine() => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "");
    // Ядро для замера по MeasureRoute: под «Весь компьютер» оно выходит к серверу через физический
    // интерфейс (PhysicalBinding), иначе мерило бы сервер через туннель.
    private ITunnelController RouteEngine() => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "") { Physical = systemTunnel is not null };

    // Чем замерять сервер: под «Весь компьютер» — его профилем с адресом, который защита
    // разрешила ядрам (иначе отдельное ядро упрётся в защиту и сервер «не ответит» за секунду);
    // null — сервера среди разрешённых нет, сейчас его не проверить.
    private Profile? MeasureRoute(Profile item) => systemTunnel is { } session ? session.PinnedFor(item) : item;

    // quiet — фоновый замер пула: без надписей, не мешает подключению.
    private Task MeasureServersAsync(IReadOnlyList<Profile> targets, bool quiet) => runtime.Measurements.MeasureAsync(targets, quiet);
    private bool measurementBusyShown;
    private string? measurementMessageShown;
    private void MeasurementsChanged()
    {
        if (closed) return;
        if (measurementBusyShown != measuring)
        {
            measurementBusyShown = measuring;
            var title = measuring ? "Остановить замер" : "Обновить пинг";
            Servers.MeasureButton.ToolTip = title;
            System.Windows.Automation.AutomationProperties.SetName(Servers.MeasureButton, title);
            UpdateControls();
        }
        if (!runtime.Measurements.Quiet && runtime.Measurements.Message.Length > 0 && measurementMessageShown != runtime.Measurements.Message)
            DetailText.Text = measurementMessageShown = runtime.Measurements.Message;
        PingsChanged();
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

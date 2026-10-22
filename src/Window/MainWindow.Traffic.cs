namespace Oblachka;

// Скорость и трафик за день в «Весь компьютер» (TrafficMeter): скорость — в строке под статусом и в
// трее, трафик за день — в подсказке к строке. В «Браузеры и программы» у ядра окна такого
// интерфейса нет, там строка прежняя.
public partial class MainWindow
{
    private TrafficMeter? meter;
    private CoreStatsEntry? meterEntry;
    private long meterCounted, trafficSavedBytes;
    private string trafficDay = "";
    private long trafficToday;
    private DateTime trafficSavedAt;

    private void RefreshTraffic()
    {
        var stats = systemTunnel?.State == SystemTunnelState.Running ? systemTunnel.Stats : null;
        if (!ReferenceEquals(stats, meterEntry))
        {
            meter?.Dispose();
            meter = stats is null ? null : new TrafficMeter(stats);
            meterEntry = stats; meterCounted = 0;
        }
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (trafficDay != today) { trafficDay = today; trafficToday = 0; }
        if (meter is not null) { var total = meter.Total; trafficToday += total - meterCounted; meterCounted = total; }
        // Счётчик дня сохраняется раз в минуту, а не на каждый тик.
        if (trafficToday != trafficSavedBytes && DateTime.UtcNow - trafficSavedAt > TimeSpan.FromMinutes(1))
        {
            trafficSavedBytes = trafficToday; trafficSavedAt = DateTime.UtcNow;
            QueueStateSave();
        }
        Home.StatusSub.ToolTip = trafficToday > 0 ? "Сегодня через ядро туннеля: " + TrafficMeter.Size(trafficToday) : null;
    }

    // Строка под статусом: имя сервера и так в карточке ниже, а скорость с ним в боковую панель не влезала.
    private string? TrafficText => meter is { Fresh: true } m ? "↓ " + TrafficMeter.Rate(m.Down) + " · ↑ " + TrafficMeter.Rate(m.Up) : null;
}

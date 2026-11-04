using System.Windows;

namespace Oblachka;

// Скорость и трафик за день в «Весь компьютер» (TrafficMeter): скорость — в строке под статусом и в
// трее, трафик за день — в подсказке к строке. В «Браузеры и программы» у ядра окна такого
// интерфейса нет, там строка прежняя.
public partial class MainWindow
{
    private TrafficMeter? meter => runtime.Traffic.Meter;
    private long meterCountedUp { get => runtime.Traffic.CountedUp; set => runtime.Traffic.CountedUp = value; }
    private long meterCountedDown { get => runtime.Traffic.CountedDown; set => runtime.Traffic.CountedDown = value; }
    private long trafficSavedBytes { get => runtime.Traffic.SavedBytes; set => runtime.Traffic.SavedBytes = value; }
    private string trafficDay { get => runtime.Workspace.TrafficDay; set => runtime.Workspace.TrafficDay = value; }
    private long trafficToday { get => runtime.Workspace.TrafficToday; set => runtime.Workspace.TrafficToday = value; }
    private TrafficHistory trafficHistory => runtime.Workspace.TrafficHistory;
    private ProgramTrafficHistory programTraffic => runtime.Traffic.Programs;
    private SpeedHistory speedHistory => runtime.Traffic.Speeds;
    private ProgramJournal programJournal => runtime.Traffic.Journal;
    private CoreConnectionSampler coreSamples => runtime.Traffic.CoreSamples;
    private CoreProgramSampler? programSamples => runtime.Traffic.ProgramSamples;
    private void RecordCoreConnections(IReadOnlyList<CoreConnection> core) => runtime.Traffic.RecordCoreConnections(core);
    private void RefreshTraffic()
    {
        if (!runtime.Traffic.Started) runtime.Traffic.Tick();
        Home.StatusSub.ToolTip = trafficToday > 0 ? "Сегодня через ядро туннеля: " + TrafficMeter.Size(trafficToday) : null;
        RefreshTodayCard();
        if (analytics?.Visibility == Visibility.Visible) RefreshAnalytics();
    }

    // Трафик за сегодня по часам (облик 0.20): 12 столбиков по два часа, последний — «сейчас».
    private long[] trafficHours => runtime.Workspace.TrafficHours;
    private string todayShown = "";
    private readonly long[] todayPairs = new long[12];
    private DateTime todayShownDay;
    private int todayShownHour = -1;

    private void RefreshTodayCard()
    {
        var text = TrafficMeter.Size(trafficToday);
        var clock = DateTime.Now; var now = clock.Hour / 2;
        var changed = text != todayShown || now != todayShownHour || clock.Date != todayShownDay;
        for (var i = 0; i < todayPairs.Length; i++)
        {
            var pair = trafficHours[i * 2] + trafficHours[i * 2 + 1];
            if (pair != todayPairs[i]) { todayPairs[i] = pair; changed = true; }
        }
        if (!changed) return;
        todayShown = text; todayShownHour = now; todayShownDay = clock.Date;
        Home.TrafficToday.Text = text;
        // Последние 12 столбиков по два часа, последний — текущий; до полуночи — пусто (счёт с начала суток).
        var window = new double[12]; var max = 1d;
        for (var i = 0; i < window.Length; i++) { var hour = now - 11 + i; window[i] = hour >= 0 ? todayPairs[hour] : 0; max = Math.Max(max, window[i]); }
        for (var i = 0; i < window.Length; i++) window[i] /= max;
        Home.TrafficBars.Values = window;
    }

    // Строка под статусом: имя сервера и так в карточке ниже, а скорость с ним в боковую панель не влезала.
    private string? TrafficText => meter is { Fresh: true } m ? "↓ " + TrafficMeter.Rate(m.Down) + " · ↑ " + TrafficMeter.Rate(m.Up) : null;
}

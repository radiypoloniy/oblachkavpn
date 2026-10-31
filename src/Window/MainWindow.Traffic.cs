using System.Windows;

namespace Oblachka;

// Скорость и трафик за день в «Весь компьютер» (TrafficMeter): скорость — в строке под статусом и в
// трее, трафик за день — в подсказке к строке. В «Браузеры и программы» у ядра окна такого
// интерфейса нет, там строка прежняя.
public partial class MainWindow
{
    private TrafficMeter? meter;
    private CoreStatsEntry? meterEntry;
    private long meterCountedUp, meterCountedDown, trafficSavedBytes;
    private string trafficDay = "";
    private long trafficToday;
    private DateTime trafficSavedAt;
    private readonly TrafficHistory trafficHistory = new();
    private readonly ProgramTrafficHistory programTraffic = new();
    private readonly SpeedHistory speedHistory = new();

    // Журнал программ для отчёта (ProgramJournal): соединения ядра туннеля раз в 5 с, и в трее тоже.
    private readonly ProgramJournal programJournal = new();
    private bool journalBusy;
    private DateTime journalAt;

    private void RecordCoreConnections(IReadOnlyList<CoreConnection> core)
    {
        programJournal.Record(core, DateTime.UtcNow);
        programTraffic.Record(core, trafficHistory, DateTime.Now);
    }

    private async Task SampleProgramsAsync(CoreStatsEntry stats)
    {
        journalBusy = true; journalAt = DateTime.UtcNow;
        try { if (await CoreConnections.ReadAsync(stats, CancellationToken.None) is { } core) RecordCoreConnections(core); }
        finally { journalBusy = false; }
    }

    private void RefreshTraffic()
    {
        var stats = systemTunnel?.State == SystemTunnelState.Running ? systemTunnel.Stats : null;
        if (!ReferenceEquals(stats, meterEntry))
        {
            meter?.Dispose();
            meter = stats is null ? null : new TrafficMeter(stats);
            meterEntry = stats; meterCountedUp = meterCountedDown = 0; programTraffic.Reset();
        }
        if (stats is not null && !journalBusy && DateTime.UtcNow - journalAt >= ProgramJournal.Interval) _ = SampleProgramsAsync(stats);
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (trafficDay != today) { trafficDay = today; trafficToday = 0; Array.Clear(trafficHours); }
        if (meter is not null)
        {
            var up = meter.TotalUp; var down = meter.TotalDown;
            if (up < meterCountedUp) meterCountedUp = 0;
            if (down < meterCountedDown) meterCountedDown = 0;
            var deltaUp = up - meterCountedUp; var deltaDown = down - meterCountedDown;
            trafficToday += deltaUp + deltaDown; trafficHours[now.Hour] += deltaUp + deltaDown;
            trafficHistory.Add(deltaUp, deltaDown, now);
            meterCountedUp = up; meterCountedDown = down;
        }
        speedHistory.Record(now, meter?.Up ?? 0, meter?.Down ?? 0);
        // Счётчик дня и история сохраняются раз в минуту, а не на каждый тик.
        if (trafficToday != trafficSavedBytes && DateTime.UtcNow - trafficSavedAt > TimeSpan.FromMinutes(1))
        {
            trafficSavedBytes = trafficToday; trafficSavedAt = DateTime.UtcNow;
            QueueStateSave();
        }
        WatchStall();
        Home.StatusSub.ToolTip = trafficToday > 0 ? "Сегодня через ядро туннеля: " + TrafficMeter.Size(trafficToday) : null;
        RefreshTodayCard();
        if (Analytics.Visibility == Visibility.Visible) RefreshAnalytics();
        CreditObservedSpeed();
    }

    // Обрыв по трафику (TrafficStall): отдача есть, приёма нет — проверка связи сразу, а не по расписанию.
    private readonly TrafficStall stall = new();
    private DateTimeOffset lastTraffic = DateTimeOffset.MinValue;
    private void WatchStall()
    {
        if (meter is not { Fresh: true } m) { stall.Reset(); return; }
        if (m.Up + m.Down > 2048) lastTraffic = DateTimeOffset.UtcNow;
        if (stall.Feed(m.Up, m.Down) && operation is null && nextHealthCheck > DateTimeOffset.UtcNow.AddSeconds(2))
            nextHealthCheck = DateTimeOffset.UtcNow;
    }

    // Трафик за сегодня по часам (облик 0.20): 12 столбиков по два часа, последний — «сейчас».
    private readonly long[] trafficHours = new long[24];
    private string todayShown = "";

    private void RefreshTodayCard()
    {
        var text = TrafficMeter.Size(trafficToday);
        var pairs = Enumerable.Range(0, 12).Select(i => trafficHours[i * 2] + trafficHours[i * 2 + 1]).ToArray();
        var now = DateTime.Now.Hour / 2;
        var key = text + "|" + now + "|" + string.Join(',', pairs);
        if (key == todayShown) return;
        todayShown = key;
        Home.TrafficToday.Text = text;
        // Последние 12 столбиков по два часа, последний — текущий; до полуночи — пусто (счёт с начала суток).
        var window = Enumerable.Range(now - 11, 12).Select(i => i >= 0 ? pairs[i] : 0L).ToArray();
        var max = Math.Max(1, window.Max());
        Home.TrafficBars.Values = window.Select(v => (double)v / max).ToArray();
    }

    // Сервер в деле качает быстрее, чем показал замер, — автовыбор узнаёт об этом: замер мог
    // попасть на загруженную минуту. Только вверх и только когда напрямую ничего не идёт: иначе в
    // счётчик ядра попали бы сайты «напрямую», а не сервер. Время замера не продлевается.
    private void CreditObservedSpeed()
    {
        if (meter is not { Fresh: true } m || m.Down < 1_000_000 || !AllThroughServer || runningProfile is not { } running) return;
        var key = Profiles.Fingerprint(running);
        if (measurements.TryGetValue(key, out var known) && known is { Success: true, Handshake: false } && m.Down > known.Throughput)
            measurements[key] = known with { Throughput = m.Down };
    }

    // Весь трафик ядра туннеля идёт через сервер: нет входа прямого трафика (российские сайты, списки,
    // zapret), правил «напрямую» и «Через VPN — только выбранное».
    private bool AllThroughServer => (engine as Engine)?.DirectEntry is null && !selectiveVpn &&
        programRules.All(r => r.Action != SiteAction.Direct) && siteRules.All(r => r.Action != SiteAction.Direct);

    // Строка под статусом: имя сервера и так в карточке ниже, а скорость с ним в боковую панель не влезала.
    private string? TrafficText => meter is { Fresh: true } m ? "↓ " + TrafficMeter.Rate(m.Down) + " · ↑ " + TrafficMeter.Rate(m.Up) : null;
}

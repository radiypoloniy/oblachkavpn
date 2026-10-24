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

    // Журнал программ для отчёта (ProgramJournal): соединения ядра туннеля раз в 5 с, и в трее тоже.
    private readonly ProgramJournal programJournal = new();
    private bool journalBusy;
    private DateTime journalAt;

    private async Task SampleProgramsAsync(CoreStatsEntry stats)
    {
        journalBusy = true; journalAt = DateTime.UtcNow;
        try { if (await CoreConnections.ReadAsync(stats, CancellationToken.None) is { } core) programJournal.Record(core, DateTime.UtcNow); }
        finally { journalBusy = false; }
    }

    private void RefreshTraffic()
    {
        var stats = systemTunnel?.State == SystemTunnelState.Running ? systemTunnel.Stats : null;
        if (!ReferenceEquals(stats, meterEntry))
        {
            meter?.Dispose();
            meter = stats is null ? null : new TrafficMeter(stats);
            meterEntry = stats; meterCounted = 0;
        }
        if (stats is not null && !journalBusy && DateTime.UtcNow - journalAt >= ProgramJournal.Interval) _ = SampleProgramsAsync(stats);
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (trafficDay != today) { trafficDay = today; trafficToday = 0; Array.Clear(trafficHours); }
        if (meter is not null)
        {
            var total = meter.Total; var delta = total - meterCounted;
            trafficToday += delta; trafficHours[DateTime.Now.Hour] += Math.Max(0, delta); meterCounted = total;
        }
        // Счётчик дня сохраняется раз в минуту, а не на каждый тик.
        if (trafficToday != trafficSavedBytes && DateTime.UtcNow - trafficSavedAt > TimeSpan.FromMinutes(1))
        {
            trafficSavedBytes = trafficToday; trafficSavedAt = DateTime.UtcNow;
            QueueStateSave();
        }
        Home.StatusSub.ToolTip = trafficToday > 0 ? "Сегодня через ядро туннеля: " + TrafficMeter.Size(trafficToday) : null;
        RefreshTodayCard();
        CreditObservedSpeed();
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

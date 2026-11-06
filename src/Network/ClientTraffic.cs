namespace Oblachka;

public sealed partial class ClientTraffic(ClientRuntime owner) : IDisposable
{
    public TrafficMeter? Meter { get; private set; }
    public CoreStatsEntry? MeterEntry { get; private set; }
    public long CountedUp, CountedDown, SavedBytes;
    private string trafficDay { get => owner.Workspace.TrafficDay; set => owner.Workspace.TrafficDay = value; }
    private long trafficToday { get => owner.Workspace.TrafficToday; set => owner.Workspace.TrafficToday = value; }
    private DateTime trafficSavedAt;
    private TrafficHistory trafficHistory => owner.Workspace.TrafficHistory;
    public ProgramTrafficHistory Programs { get; } = new();
    public SpeedHistory Speeds { get; } = new();

    // Журнал программ для отчёта (ProgramJournal): соединения ядра туннеля раз в 5 с, и в трее тоже.
    public ProgramJournal Journal { get; } = new();
    internal CoreConnectionSampler CoreSamples { get; } = new();
    internal CoreProgramSampler? ProgramSamples { get; private set; }
    private CoreStatsEntry? programEntry;
    private bool journalBusy;
    private DateTime journalAt;

    public void RecordCoreConnections(IReadOnlyList<CoreConnection> core)
    {
        Journal.Record(core, DateTime.UtcNow);
        Programs.Record(core, trafficHistory, DateTime.Now);
    }

    private async Task SampleProgramsAsync(CoreStatsEntry stats)
    {
        journalBusy = true; journalAt = DateTime.UtcNow;
        try
        {
            if (ProgramSamples is null) return;
            var session = owner.Tunnel;
            var sample = await ProgramSamples.ReadAsync(stats, shutdown.Token);
            if (disposed || !ReferenceEquals(stats, MeterEntry) || !ReferenceEquals(session, owner.Tunnel)) return;
            if (sample?.Summary is { } summary)
            {
                Journal.Record(summary, DateTime.UtcNow);
                Programs.Record(summary, trafficHistory, DateTime.Now, index =>
                {
                    if (session?.TrafficSourceAt(index) is not { } source) return null;
                    var group = owner.Profiles.Groups.FirstOrDefault(group => group.Id == source.GroupId);
                    return source with { GroupName = group?.Name };
                });
            }
            else if (sample?.Connections is { } core) RecordCoreConnections(core);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UserError or ObjectDisposedException) { }
        finally { journalBusy = false; }
    }

    public void Tick()
    {
        if (disposed) return;
        var session = owner.Tunnel;
        var stats = session?.State == SystemTunnelState.Running ? session.Stats : null;
        // Holding during a server switch keeps the same core and its cumulative
        // counters. Retain the source and baseline until the session actually changes.
        if (!ReferenceEquals(programEntry, session?.Stats))
        {
            programEntry = session?.Stats;
            ProgramSamples = programEntry is null ? null : new CoreProgramSampler(CoreSamples.ReadAsync);
            Programs.Reset();
        }
        if (!ReferenceEquals(stats, MeterEntry))
        {
            CoreSamples.Reset();
            Meter?.Dispose();
            Meter = stats is null ? null : new TrafficMeter(stats);
            MeterEntry = stats; CountedUp = CountedDown = 0;
        }
        if (stats is not null && !journalBusy && DateTime.UtcNow - journalAt >= ProgramJournal.Interval) _ = SampleProgramsAsync(stats);
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (trafficDay != today) { trafficDay = today; trafficToday = 0; Array.Clear(owner.Workspace.TrafficHours); }
        if (Meter is not null)
        {
            var up = Meter.TotalUp; var down = Meter.TotalDown;
            if (up < CountedUp) CountedUp = 0;
            if (down < CountedDown) CountedDown = 0;
            var deltaUp = up - CountedUp; var deltaDown = down - CountedDown;
            trafficToday += deltaUp + deltaDown; owner.Workspace.TrafficHours[now.Hour] += deltaUp + deltaDown;
            trafficHistory.Add(deltaUp, deltaDown, now);
            CountedUp = up; CountedDown = down;
        }
        Speeds.Record(now, Meter?.Up ?? 0, Meter?.Down ?? 0);
        // Счётчик дня и история сохраняются раз в минуту, а не на каждый тик.
        if (trafficToday != SavedBytes && DateTime.UtcNow - trafficSavedAt > TimeSpan.FromMinutes(1))
        {
            SavedBytes = trafficToday; trafficSavedAt = DateTime.UtcNow;
            owner.Persistence.Queue();
        }
        WatchStall();
        CreditObservedSpeed();
    }

    // Обрыв по трафику (TrafficStall): отдача есть, приёма нет — проверка связи сразу, а не по расписанию.
    private readonly TrafficStall stall = new();
    private void WatchStall()
    {
        if (Meter is not { Fresh: true } m) { stall.Reset(); return; }
        if (m.Up + m.Down > 2048) owner.Monitor.LastTraffic = DateTimeOffset.UtcNow;
        if (stall.Feed(m.Up, m.Down) && owner.Activity.Operation is null && owner.Monitor.NextCheck > DateTimeOffset.UtcNow.AddSeconds(2))
            owner.Monitor.NextCheck = DateTimeOffset.UtcNow;
    }

    private void CreditObservedSpeed()
    {
        if (Meter is not { Fresh: true } m || m.Down < 1_000_000 || !AllThroughServer || owner.Profiles.Running is not { } running) return;
        var key = Profiles.Fingerprint(running);
        if (owner.Profiles.Measurements.TryGetValue(key, out var known) && known is { Success: true, Handshake: false } && m.Down > known.Throughput)
            owner.Profiles.Measurements[key] = known with { Throughput = m.Down };
    }

    // Весь трафик ядра туннеля идёт через сервер: нет входа прямого трафика (российские сайты, списки,
    // zapret), правил «напрямую» и «Через VPN — только выбранное».
    private bool AllThroughServer => (owner.Engine as Engine)?.DirectEntry is null && !owner.TunnelPolicy.Selective &&
        owner.TunnelPolicy.Programs.All(r => r.Action != SiteAction.Direct) && owner.TunnelPolicy.Sites.All(r => r.Action != SiteAction.Direct);

}

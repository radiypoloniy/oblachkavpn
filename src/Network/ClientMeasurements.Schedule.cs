namespace Oblachka;

public sealed partial class ClientMeasurements
{
    private SynchronizationContext? context;
    private Timer? timer;
    private readonly object lifecycle = new();
    private int pending, scheduleEpoch;
    private Queue<string>? backgroundQueue;
    private DateTimeOffset nextMeasurement = DateTimeOffset.MinValue;
    public bool Enabled { get; internal set; }
    internal const int BackgroundBatchSize = 8;
    public void Start(SynchronizationContext context)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (timer is not null) return;
            this.context = context; Enabled = true;
            owner.TunnelChanged += TunnelChanged; owner.Engine.Changed += CoreChanged;
            timer = new Timer(_ => QueueTick(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }
    public async Task PollAsync()
    {
        if (disposed || !Enabled || owner.Activity.VmTestActive || owner.Profiles.Imported.Count == 0) return;
        var age = now() - LastPing;
        if (age >= TimeSpan.FromMinutes(10) || owner.Profiles.Pings.Count == 0 && age >= TimeSpan.FromSeconds(30)) QueuePing();
        if (IsMeasuring || owner.Activity.MeasuringTrial || owner.Activity.Operation is not null || owner.Activity.RecoveryWait is not null ||
            owner.Connections.IsActive || owner.Connections.IsStopping || now() < nextMeasurement) return;
        var pool = owner.Profiles.AutoPool.Members(owner.Profiles.Imported.ToArray()).GroupBy(Profiles.Fingerprint).ToDictionary(g => g.Key, g => g.First());
        backgroundQueue ??= new Queue<string>(pool.Keys);
        backgroundQueue = new Queue<string>(backgroundQueue.Where(pool.ContainsKey));
        var queue = backgroundQueue; var epoch = scheduleEpoch;
        var batch = queue.Take(BackgroundBatchSize).Select(key => pool[key]).ToArray();
        if (batch.Length == 0) { backgroundQueue = null; nextMeasurement = now().AddMinutes(10); return; }
        void Starting(Profile p)
        {
            if (epoch == scheduleEpoch && queue.TryPeek(out var key) && key == Profiles.Fingerprint(p)) queue.Dequeue();
        }
        try { await (MeasureTask = MeasureCoreAsync(batch, true, CancellationToken.None, true, Starting)); }
        finally
        {
            if (epoch == scheduleEpoch)
            {
                nextMeasurement = now() + (queue.Count > 0 ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(10));
                if (queue.Count == 0) backgroundQueue = null;
            }
        }
    }
    internal TimeSpan NextDelay()
    {
        if (disposed || !Enabled || owner.Profiles.Imported.Count == 0) return Timeout.InfiniteTimeSpan;
        var pingDue = CanPing && pinging is null ? LastPing.Add(owner.Profiles.Pings.Count == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10)) : DateTimeOffset.MaxValue;
        var measureDue = IsMeasuring ? DateTimeOffset.MaxValue : nextMeasurement;
        var due = pingDue < measureDue ? pingDue : measureDue;
        if (due == DateTimeOffset.MaxValue) return Timeout.InfiniteTimeSpan;
        var delay = due - now();
        if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
        if (delay <= TimeSpan.Zero) return TimeSpan.FromSeconds(30);
        return delay;
    }
    private void QueueTick()
    {
        if (Interlocked.Exchange(ref pending, 1) != 0) return;
        context?.Post(async _ =>
        {
            try { await PollAsync(); }
            finally { Interlocked.Exchange(ref pending, 0); Schedule(NextDelay()); }
        }, null);
    }
    private void Schedule(TimeSpan delay)
    { lock (lifecycle) { if (!disposed) timer?.Change(delay, Timeout.InfiniteTimeSpan); } }
    private void StopSchedule()
    {
        lock (lifecycle)
        {
            if (timer is not null) { owner.TunnelChanged -= TunnelChanged; owner.Engine.Changed -= CoreChanged; }
            timer?.Dispose(); timer = null; Enabled = false;
        }
    }
    private void TunnelChanged(SystemTunnelState state) => Schedule(TimeSpan.FromSeconds(1));
    private void CoreChanged(ConnectionState state, string message)
    { if (state is ConnectionState.Idle or ConnectionState.Failed) Schedule(TimeSpan.FromSeconds(1)); }
}

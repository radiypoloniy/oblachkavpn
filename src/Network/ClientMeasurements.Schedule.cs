namespace Oblachka;

public sealed partial class ClientMeasurements
{
    private SynchronizationContext? context;
    private Timer? timer;
    private readonly object lifecycle = new();
    private int pending;
    public bool Enabled { get; internal set; }
    public void Start(SynchronizationContext context)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (timer is not null) return;
            this.context = context; Enabled = true;
            timer = new Timer(_ => QueueTick(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }
    public async Task PollAsync()
    {
        if (disposed || !Enabled || owner.Activity.VmTestActive || owner.Profiles.Imported.Count == 0) return;
        var age = now() - LastPing;
        if (age > TimeSpan.FromMinutes(10) || owner.Profiles.Pings.Count == 0 && age > TimeSpan.FromSeconds(30)) QueuePing();
        if (!IsMeasuring && !owner.Activity.MeasuringTrial && owner.Activity.Operation is null && owner.Activity.RecoveryWait is null &&
            !owner.Connections.IsActive && !owner.Connections.IsStopping && now() - LastMeasure > TimeSpan.FromMinutes(10))
            await MeasureAsync(owner.Profiles.AutoPool.Members(owner.Profiles.Imported.ToArray()), true);
    }
    private void QueueTick()
    {
        if (Interlocked.Exchange(ref pending, 1) != 0) return;
        context?.Post(async _ =>
        {
            try { await PollAsync(); }
            finally { Interlocked.Exchange(ref pending, 0); Schedule(owner.Profiles.Imported.Count == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(30)); }
        }, null);
    }
    private void Schedule(TimeSpan delay)
    { lock (lifecycle) { if (!disposed) timer?.Change(delay, Timeout.InfiniteTimeSpan); } }
    private void StopSchedule()
    { lock (lifecycle) { timer?.Dispose(); timer = null; Enabled = false; } }
}

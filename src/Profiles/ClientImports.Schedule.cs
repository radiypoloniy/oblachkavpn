namespace Oblachka;

public sealed partial class ClientImports
{
    private readonly Dictionary<string, SubscriptionSchedule> schedules = new();
    private readonly object lifecycle = new();
    private SynchronizationContext? context;
    private Timer? timer;
    private bool polling;
    private int tickPending;
    internal SubscriptionSchedule ScheduleFor(string id)
    {
        if (!schedules.TryGetValue(id, out var schedule)) schedules[id] = schedule = new();
        return schedule;
    }
    public void Start(SynchronizationContext context)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (timer is not null) return;
            this.context = context;
            owner.Engine.Changed += EngineChanged;
            owner.Preferences.PropertyChanged += PreferencesChanged;
            timer = new Timer(_ => QueueTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        Wake();
    }
    public async Task PollAsync()
    {
        if (disposed || polling || owner.Activity.VmTestActive || !owner.Preferences.AutoRefresh ||
            owner.Activity.Operation is not null || owner.Activity.RecoveryWait is not null || owner.Connections.IsActive || owner.Connections.IsStopping) return;
        polling = true;
        try
        {
            foreach (var group in owner.Profiles.Groups.Where(g => g.IsSubscription).ToArray())
            {
                if (disposed || owner.Activity.Operation is not null || owner.Profiles.Groups.All(g => g.Id != group.Id)) return;
                if (Pending?.Group.Id == group.Id || ForeignConnected && group.Source == ProfileSource.Service) continue;
                if (ScheduleFor(group.Id).IsDue(now())) { await LoadAsync(group.Sources, group.PreferredSource); if (owner.Activity.ManualStop) return; }
            }
        }
        finally { polling = false; }
    }
    private void QueueTick()
    {
        if (Interlocked.Exchange(ref tickPending, 1) != 0) return;
        context?.Post(async _ =>
        {
            try { await PollAsync(); }
            finally { Interlocked.Exchange(ref tickPending, 0); Wake(); }
        }, null);
    }
    private void Wake()
    {
        lock (lifecycle)
        {
            if (disposed || timer is null) return;
            var due = !owner.Preferences.AutoRefresh ? DateTimeOffset.MaxValue : owner.Profiles.Groups
                .Where(g => g.IsSubscription && Pending?.Group.Id != g.Id && !(ForeignConnected && g.Source == ProfileSource.Service))
                .Select(g => ScheduleFor(g.Id).Due).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
            var delay = due == DateTimeOffset.MaxValue ? Timeout.InfiniteTimeSpan : due - now();
            if (due != DateTimeOffset.MaxValue && delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(5);
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }
    private void EngineChanged(ConnectionState state, string message) => context?.Post(_ =>
    { if (!disposed) { TryApplyPending(); Wake(); } }, null);
    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(ClientPreferences.AutoRefresh)) Wake(); }
    private void StopSchedule()
    {
        lock (lifecycle)
        {
            if (timer is null) return;
            owner.Engine.Changed -= EngineChanged; owner.Preferences.PropertyChanged -= PreferencesChanged;
            timer.Dispose(); timer = null;
        }
    }
}

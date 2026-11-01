using Microsoft.Win32;
using System.Net.NetworkInformation;

namespace Oblachka;

public sealed partial class ClientHealthMonitor
{
    private SynchronizationContext? context;
    private Timer? timer;
    private readonly object lifecycle = new();
    private int pending;
    public void Start(SynchronizationContext context)
    {
        lock (lifecycle)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ClientHealthMonitor));
            if (timer is not null) return;
            this.context = context;
            owner.Engine.Changed += CoreChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
            timer = new Timer(_ => QueueTick(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
    private void Wake() => Schedule(TimeSpan.Zero);
    private void Schedule(TimeSpan delay)
    { lock (lifecycle) { if (!disposed) timer?.Change(delay, Timeout.InfiniteTimeSpan); } }
    private void QueueTick()
    {
        if (disposed || Interlocked.Exchange(ref pending, 1) != 0) return;
        context?.Post(async _ =>
        {
            try
            {
                if (!disposed)
                {
                    ObserveProcess(); var before = LastObservation;
                    await PollAsync();
                    if (!disposed && LastObservation is { ShouldRecover: true } result && !ReferenceEquals(before, result))
                        await owner.Recovery.RecoverAsync();
                    else if (!disposed && owner.Engine.State == ConnectionState.Failed)
                        await owner.Recovery.RestartFailedAsync();
                }
            }
            finally
            {
                Interlocked.Exchange(ref pending, 0);
                if (!disposed)
                {
                    var delay = next - now();
                    if (owner.Activity.Operation is not null || owner.Activity.RecoveryWait is not null) delay = TimeSpan.FromSeconds(1);
                    if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                    if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
                    if (owner.Engine.ProcessId is null || next == DateTimeOffset.MaxValue) delay = Timeout.InfiniteTimeSpan;
                    Schedule(delay);
                }
            }
        }, null);
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs args) { if (args.Mode == PowerModes.Resume) RequestSoon(); }
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) { if (args.IsAvailable) RequestSoon(); }
    private void CoreChanged(ConnectionState state, string message) => context?.Post(_ =>
    { if (!disposed) { ObserveProcess(); Wake(); if (state == ConnectionState.Failed) _ = owner.Recovery.RestartFailedAsync(); } }, null);
    private void RequestSoon() => context?.Post(_ => { if (!disposed) NextCheck = now().AddSeconds(3); }, null);
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return;
            disposed = true;
            if (timer is not null)
            {
                owner.Engine.Changed -= CoreChanged;
                SystemEvents.PowerModeChanged -= PowerChanged;
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
                timer.Dispose(); timer = null;
            }
        }
        probing?.Cancel();
        StateChanged = null; Completed = null;
    }
}

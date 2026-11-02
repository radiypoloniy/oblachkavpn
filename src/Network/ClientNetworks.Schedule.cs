using Microsoft.Win32;

namespace Oblachka;

public sealed partial class ClientNetworks
{
    private SynchronizationContext? context;
    private Timer? timer;
    private readonly object lifecycle = new();
    private int queued;
    public void Start(SynchronizationContext context)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (timer is not null) return;
            this.context = context;
            timer = new Timer(_ => context.Post(async _ => { if (!disposed) await ProcessAsync(); }, null), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += AddressChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
        }
    }
    private void AddressChanged(object? sender, EventArgs args) => RequestChange();
    private void PowerChanged(object sender, PowerModeChangedEventArgs args) { if (args.Mode == PowerModes.Resume) RequestChange(); }
    public void RequestChange()
    {
        if (Interlocked.Exchange(ref queued, 1) != 0) return;
        context?.Post(_ =>
        {
            Interlocked.Exchange(ref queued, 0);
            if (!disposed) { epoch++; Schedule(TimeSpan.FromSeconds(3)); }
        }, null);
    }
    private void Schedule(TimeSpan delay)
    { lock (lifecycle) { if (!disposed) timer?.Change(delay, Timeout.InfiniteTimeSpan); } }
    private void StopSchedule()
    {
        lock (lifecycle)
        {
            if (timer is null) return;
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= AddressChanged;
            SystemEvents.PowerModeChanged -= PowerChanged; timer.Dispose(); timer = null;
        }
    }
}

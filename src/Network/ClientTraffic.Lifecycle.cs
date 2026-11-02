using Microsoft.Win32;

namespace Oblachka;

public sealed partial class ClientTraffic
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly object lifecycle = new();
    private Timer? timer;
    private bool disposed;
    private int pending, wakeVersion;
    public bool Started => timer is not null;
    public void Start(SynchronizationContext context)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (timer is not null) return;
            owner.TunnelChanged += TunnelChanged; owner.Engine.Changed += EngineChanged;
            SystemEvents.TimeChanged += TimeChanged; SystemEvents.PowerModeChanged += PowerChanged;
            timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref pending, 1) != 0) return;
                context.Post(_ =>
                {
                    var version = Volatile.Read(ref wakeVersion);
                    try { if (!disposed) Tick(); }
                    finally
                    {
                        Interlocked.Exchange(ref pending, 0);
                        lock (lifecycle)
                            if (!disposed) timer?.Change(version == Volatile.Read(ref wakeVersion) ? NextDelay(DateTime.Now) : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                    }
                }, null);
            }, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
    internal TimeSpan NextDelay(DateTime localNow) => owner.Tunnel is { State: SystemTunnelState.Running, Stats: not null }
        ? TimeSpan.FromSeconds(1) : localNow.Date.AddDays(1) - localNow;
    private void Wake()
    {
        Interlocked.Increment(ref wakeVersion);
        lock (lifecycle) { if (!disposed) timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); }
    }
    private void TunnelChanged(SystemTunnelState state) => Wake();
    private void EngineChanged(ConnectionState state, string message) => Wake();
    private void TimeChanged(object? sender, EventArgs args) => Wake();
    private void PowerChanged(object sender, PowerModeChangedEventArgs args) { if (args.Mode == PowerModes.Resume) Wake(); }
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return;
            disposed = true; timer?.Dispose(); timer = null;
            owner.TunnelChanged -= TunnelChanged; owner.Engine.Changed -= EngineChanged;
            SystemEvents.TimeChanged -= TimeChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        }
        shutdown.Cancel(); Meter?.Dispose(); Meter = null; MeterEntry = null;
    }
}

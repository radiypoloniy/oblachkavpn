namespace Oblachka;

public sealed partial class ClientTraffic
{
    private readonly CancellationTokenSource shutdown = new();
    private Timer? timer;
    private bool disposed;
    private int pending;
    public bool Started => timer is not null;
    public void Start(SynchronizationContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (timer is not null) return;
        timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref pending, 1) != 0) return;
            context.Post(_ =>
            {
                try { if (!disposed) Tick(); }
                finally { Interlocked.Exchange(ref pending, 0); }
            }, null);
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer?.Dispose(); timer = null; shutdown.Cancel();
        Meter?.Dispose(); Meter = null; MeterEntry = null;
    }
}

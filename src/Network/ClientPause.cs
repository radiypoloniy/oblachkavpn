using Microsoft.Win32;

namespace Oblachka;

// Срок и возобновление принадлежат приложению; таймер не удерживает окно и спит до срока.
public sealed class ClientPause : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly Func<DateTimeOffset> now;
    private readonly object lifecycle = new();
    private SynchronizationContext? context;
    private Timer? timer;
    private bool disposed, polling;
    private int pending;
    public DateTimeOffset? Until { get; private set; }
    public event Action? Changed;
    public event Action? Resuming;
    public event Action? Resumed;
    public event Action? ResumeFailed;
    internal ClientPause(ClientRuntime owner, Func<DateTimeOffset>? now = null)
    { this.owner = owner; this.now = now ?? (() => DateTimeOffset.UtcNow); }

    public void Start(SynchronizationContext context)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (timer is not null) return;
            this.context = context;
            timer = new Timer(_ => QueueTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            SystemEvents.PowerModeChanged += PowerChanged;
        }
        Schedule();
    }
    public async Task PauseAsync(int minutes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (minutes is not (5 or 15 or 60)) throw new UserError("Выберите паузу на 5, 15 или 60 минут.");
        if (owner.Engine.State is not (ConnectionState.Running or ConnectionState.Verified) || owner.Connections.IsActive || owner.Connections.IsStopping)
            throw new UserError("Пауза доступна после завершения подключения.");
        var deadline = now().AddMinutes(minutes);
        SetDeadline(deadline);
        try { await owner.Connections.StopAsync(preservePause: true); }
        finally
        {
            if (Until == deadline && !disposed) { owner.Workspace.WasConnected = true; owner.Persistence.Queue(); Schedule(); }
        }
    }
    internal void SetDeadline(DateTimeOffset? deadline)
    { Until = deadline; Schedule(); if (!disposed) Changed?.Invoke(); }
    public void Cancel(bool rememberDisconnected = true)
    {
        if (Until is null) return;
        SetDeadline(null);
        if (rememberDisconnected) { owner.Workspace.WasConnected = false; owner.Persistence.Queue(); }
    }
    public async Task PollAsync()
    {
        if (disposed || polling || Until is not { } deadline || now() < deadline) return;
        if (owner.Connections.IsActive || owner.Connections.IsStopping || owner.Activity.Operation is not null ||
            owner.Activity.RecoveryWait is not null || owner.Activity.VmTestActive) { Schedule(); return; }
        if (owner.Engine.State is ConnectionState.Running or ConnectionState.Verified || owner.Tunnel is not null)
        { SetDeadline(null); return; }
        var target = owner.Selection.BeforeConnect();
        if (target is null) { Schedule(); return; }
        polling = true; SetDeadline(null);
        try { Resuming?.Invoke(); await owner.Connections.StartAsync(target); if (!disposed) Resumed?.Invoke(); }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
        { if (!disposed) ResumeFailed?.Invoke(); }
        finally { polling = false; if (!disposed) Changed?.Invoke(); }
    }
    private void Schedule()
    {
        lock (lifecycle)
        {
            if (disposed || timer is null) return;
            var delay = Until is { } until ? until - now() : Timeout.InfiniteTimeSpan;
            if (Until is not null && delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(1);
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }
    private void QueueTick()
    {
        if (Interlocked.Exchange(ref pending, 1) != 0) return;
        context?.Post(async _ =>
        {
            try { await PollAsync(); }
            finally { Interlocked.Exchange(ref pending, 0); Schedule(); }
        }, null);
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    { if (args.Mode == PowerModes.Resume) QueueTick(); }
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return;
            disposed = true;
            if (timer is not null) { SystemEvents.PowerModeChanged -= PowerChanged; timer.Dispose(); timer = null; }
        }
        Until = null; Changed = null; Resuming = null; Resumed = null; ResumeFailed = null;
    }
}

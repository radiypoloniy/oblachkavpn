namespace Oblachka;

// Проверка и расписание принадлежат приложению; установку по-прежнему запускает пользователь.
public sealed class ClientUpdates : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly Func<bool, CancellationToken, Task<AppRelease?>> check;
    private readonly Func<AppRelease, bool> newer;
    private readonly Action cleanup;
    private readonly object lifecycle = new();
    private SynchronizationContext? context;
    private Timer? timer;
    private CancellationTokenSource? checking;
    private bool disposed, announced;
    private int channel;
    public bool Installing { get; set; }
    public bool Busy => checking is not null || Installing;
    public AppRelease? Available { get; private set; }
    public bool Checked { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public event Action<AppRelease>? AvailableInBackground;
    internal ClientUpdates(ClientRuntime owner, Func<bool, CancellationToken, Task<AppRelease?>>? check = null,
        Func<AppRelease, bool>? newer = null, Action? cleanup = null)
    {
        this.owner = owner; this.check = check ?? AppUpdate.CheckAsync; this.newer = newer ?? AppUpdate.IsNewer;
        this.cleanup = cleanup ?? AppUpdate.CleanUp;
        owner.Preferences.PropertyChanged += PreferencesChanged;
    }
    public void Start(SynchronizationContext context, TimeSpan? initialDelay = null)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (this.context is not null) return;
            this.context = context; cleanup();
            timer = new Timer(_ => context.Post(async _ =>
            {
                if (disposed) return;
                try { if (owner.Preferences.UpdateAuto) await CheckAsync(manual: false); }
                finally { Schedule(TimeSpan.FromHours(12)); }
            }, null), null, Timeout.Infinite, Timeout.Infinite);
            Schedule(initialDelay ?? TimeSpan.FromSeconds(30));
        }
    }
    private void Schedule(TimeSpan delay)
    {
        lock (lifecycle)
        {
            if (disposed || timer is null) return;
            timer.Change(owner.Preferences.UpdateAuto ? delay : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ClientPreferences.UpdatePrerelease))
        { channel++; Available = null; Checked = false; Error = null; Changed?.Invoke(); }
        if (args.PropertyName == nameof(ClientPreferences.UpdateAuto)) Schedule(TimeSpan.FromSeconds(30));
    }
    public async Task CheckAsync(bool manual)
    {
        CancellationTokenSource cancel;
        int version;
        lock (lifecycle)
        {
            if (disposed || Busy) return;
            checking = cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30)); version = channel;
        }
        try
        {
            Changed?.Invoke();
            var release = await check(owner.Preferences.UpdatePrerelease, cancel.Token);
            if (disposed || version != channel) return;
            Available = release is not null && newer(release) ? release : null;
            Error = null; Checked = true;
            if (Available is { Signature: not null } signed && !manual && !announced)
            { announced = true; AvailableInBackground?.Invoke(signed); }
        }
        catch (Exception error) when (error is UserError or HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
        { if (!disposed && version == channel) Error = manual ? error is UserError ? error.Message : "Не удалось связаться с GitHub. Проверьте интернет и попробуйте ещё раз." : null; }
        finally
        {
            lock (lifecycle) { checking = null; cancel.Dispose(); }
            if (!disposed) Changed?.Invoke();
        }
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return; disposed = true;
            owner.Preferences.PropertyChanged -= PreferencesChanged;
            timer?.Dispose(); timer = null;
            checking?.Cancel();
        }
        Changed = null; AvailableInBackground = null;
    }
}

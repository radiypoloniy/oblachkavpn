namespace Oblachka;

// Хранение и отложенная запись принадлежат приложению, а не времени жизни окна.
public sealed class ClientPersistence : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly object lifecycle = new();
    private SynchronizationContext? context;
    private Timer? timer;
    private bool configured, disposed;
    public ClientStateStore? Store { get; private set; }
    public bool Isolated { get; private set; }
    public bool Restored { get; set; }
    public bool Restoring { get; set; }
    public bool Dirty { get; set; }
    public bool Remember { get; set; } = true;
    public string? LastError { get; private set; }
    public event Action? Changed;
    internal ClientPersistence(ClientRuntime owner) { this.owner = owner; }
    public void Configure(ClientStateStore? store, bool isolated)
    {
        if (configured) return;
        configured = true; Store = store; Isolated = isolated;
    }
    public ClientSnapshot Capture() => ClientSnapshotBuilder.Capture(owner);
    public void Start(SynchronizationContext context)
    { this.context ??= context; if (Dirty) Schedule(); }
    public void Queue()
    {
        if (disposed || Store is null || Restoring || !Remember || !(Restored || owner.Profiles.Selected is not null)) return;
        Dirty = true; Schedule();
    }
    private void Schedule(TimeSpan? delay = null)
    {
        lock (lifecycle)
        {
            if (disposed || context is null) return;
            timer ??= new Timer(_ => context.Post(_ => Tick(), null), null, Timeout.Infinite, Timeout.Infinite);
            timer.Change(delay ?? TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }
    private void Tick()
    {
        if (disposed) return;
        Flush();
        if (Dirty && Remember && Store is not null) Schedule(LastError is null ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30));
    }
    public bool Flush()
    {
        if (!Dirty || Restoring || Store is null || !Remember || !(Restored || owner.Profiles.Selected is not null) || owner.Activity.Operation is not null) return false;
        try { SaveNow(); return true; }
        catch (UserError error) { LastError = error.Message; Changed?.Invoke(); return false; }
    }
    public void SaveNow()
    {
        if (Store is null || owner.Activity.Operation is not null) throw new UserError("Сейчас сохранить состояние нельзя. Дождитесь окончания операции.");
        Store.Save(Capture()); Dirty = false; LastError = null; Changed?.Invoke();
    }
    public ClientSnapshot? LoadOnce()
    {
        if (Restored || Store is null) return null;
        Restored = true;
        return Store.LoadWithMigration(Isolated ? [] : ClientDataPaths.LegacyFiles);
    }
    public void Dispose()
    {
        if (disposed) return;
        Flush();
        lock (lifecycle) { disposed = true; timer?.Dispose(); timer = null; }
        Changed = null;
    }
}

using Microsoft.Win32;

namespace Oblachka;

public enum ProxyChangeKind { Restored, Repaired, Lost, Error }
public sealed record ProxyChange(ProxyChangeKind Kind, string Message);

// Настройки Windows и их восстановление живут столько же, сколько приложение.
public sealed class ClientProxy : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly Func<DateTimeOffset> now;
    private readonly object gate = new();
    private readonly Queue<DateTimeOffset> repairs = new();
    private SystemProxy? backend;
    private SynchronizationContext? context;
    private Timer? timer;
    private bool disposed, sessionEvents;
    private DateTimeOffset next;
    public int Port { get; private set; }
    public bool GaveUp { get; private set; }
    public event Action<ProxyChange>? Changed;
    internal ClientProxy(ClientRuntime owner, Func<DateTimeOffset>? now = null) { this.owner = owner; this.now = now ?? (() => DateTimeOffset.UtcNow); }
    public void Start(SynchronizationContext context, SystemProxy? proxy = null, bool subscribeSessionEvents = true)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (backend is not null) return;
            this.context = context; backend = proxy ?? SystemProxy.ForCurrentUser();
            owner.Engine.Changed += EngineChanged; owner.Preferences.PropertyChanged += PreferencesChanged;
            if (sessionEvents = subscribeSessionEvents) SystemEvents.SessionEnding += SessionEnding;
        }
        Try(() => { if (backend.Restore()) Publish(ProxyChangeKind.Restored, "Восстановлены прежние настройки прокси Windows после прошлого запуска."); });
        Sync();
    }
    private void EngineChanged(ConnectionState state, string message) => context?.Post(_ => Sync(), null);
    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ClientPreferences.SystemProxy) or nameof(ClientPreferences.SystemTunnel))
            context?.Post(_ => Sync(), null);
    }
    public void Sync()
    {
        Try(() =>
        {
            if (disposed || backend is null) return;
            var state = owner.Engine.State;
            var wanted = owner.Preferences.SystemProxy && !owner.Preferences.SystemTunnel &&
                state is ConnectionState.Running or ConnectionState.Verified && owner.Engine.Port > 0;
            if (wanted && Port != owner.Engine.Port) { backend.Apply(owner.Engine.Port); Port = owner.Engine.Port; }
            // При отказе ядра прокси сохраняется: браузер не переходит напрямую во время восстановления.
            else if (!wanted && (state == ConnectionState.Idle || !owner.Preferences.SystemProxy || owner.Preferences.SystemTunnel) && (Port != 0 || backend.IsApplied))
            { backend.Restore(); Port = 0; GaveUp = false; }
            Schedule();
        });
    }
    private void Schedule()
    {
        if (disposed || context is null) return;
        if (Port == 0 || GaveUp) { timer?.Change(Timeout.Infinite, Timeout.Infinite); return; }
        timer ??= new Timer(_ => context.Post(_ => Poll(), null), null, Timeout.Infinite, Timeout.Infinite);
        // Timer может разбудить чуть раньше срока: ждём остаток, а не ещё полные пять секунд.
        var delay = next == default ? TimeSpan.FromSeconds(5) : next - now();
        if (delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);
        if (delay > TimeSpan.FromSeconds(5)) delay = TimeSpan.FromSeconds(5);
        timer.Change(delay, Timeout.InfiniteTimeSpan);
    }
    public void Poll()
    {
        Try(() =>
        {
            if (disposed || backend is null || Port == 0 || GaveUp) return;
            var time = now();
            if (time < next) { Schedule(); return; }
            next = time.AddSeconds(5);
            try
            {
                if (!backend.Overwritten()) return;
                while (repairs.Count > 0 && time - repairs.Peek() > TimeSpan.FromMinutes(10)) repairs.Dequeue();
                if (repairs.Count >= 3)
                {
                    GaveUp = true;
                    Publish(ProxyChangeKind.Lost, "Другая программа раз за разом меняет прокси Windows — браузеры идут мимо VPN. Закройте её или включите «Весь компьютер».");
                    return;
                }
                repairs.Enqueue(time); backend.Apply(Port);
                Publish(ProxyChangeKind.Repaired, "Прокси Windows поменяла другая программа — Облачка вернула свой, иначе браузеры шли бы мимо VPN.");
            }
            finally { Schedule(); }
        });
    }
    private void Publish(ProxyChangeKind kind, string message) => Changed?.Invoke(new(kind, message));
    private void Try(Action action)
    {
        try { lock (gate) action(); }
        catch (Exception error) when (error is UserError or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Publish(ProxyChangeKind.Error, error is UserError ? error.Message : "Не удалось изменить настройки прокси Windows. Проверьте их вручную: Параметры → Сеть и Интернет → Прокси."); }
    }
    private void SessionEnding(object? sender, SessionEndingEventArgs args) => Try(() => { backend?.Restore(); Port = 0; timer?.Change(Timeout.Infinite, Timeout.Infinite); });
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            owner.Engine.Changed -= EngineChanged; owner.Preferences.PropertyChanged -= PreferencesChanged;
            if (sessionEvents) SystemEvents.SessionEnding -= SessionEnding;
            timer?.Dispose(); timer = null;
            Try(() => backend?.Restore()); Port = 0; Changed = null;
        }
    }
}

namespace Oblachka;

// Сетевые ресурсы приложения не принадлежат WPF-окну. Штатное закрытие сначала снимает туннель.
public sealed class ClientRuntime : IDisposable
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    public ITunnelController Engine { get; }
    public ClientProfiles Profiles { get; } = new();
    public ClientPreferences Preferences { get; } = new();
    public ClientActivity Activity { get; } = new();
    public ClientWorkspace Workspace { get; } = new();
    public ClientPersistence Persistence { get; }
    public ClientProxy Proxy { get; }
    public ClientUpdates Updates { get; }
    internal ClientTray? Tray { get; private set; }
    internal ClientTray EnsureTray(SynchronizationContext context) => Tray ??= new(this, context);
    public ClientHealthMonitor Monitor { get; }
    public ClientRecovery Recovery { get; }
    public ClientTunnelPolicy TunnelPolicy { get; }
    public ClientTunnels Tunnels { get; }
    private readonly IClientTunnelHost tunnelHost;
    internal IClientTunnelLink? TunnelLink { get; private set; }
    internal bool ServiceAvailable => tunnelHost.IsService;
    public RoutingListsEntry? RoutingLists { get; set; }
    public SystemTunnelSession? Tunnel { get; private set; }
    public IHelperLifetime? Helper { get; private set; }
    public event Action<SystemTunnelState>? TunnelChanged;
    public event Action? ProtectionRepaired;
    private Task stopping = Task.CompletedTask;
    private readonly Func<Profile, ServerTweak?> previousLookup, lookup;
    private bool disposed;

    public ClientRuntime() : this(new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"),
        Path.Combine(AppContext.BaseDirectory, "data", "runtime"))) { }
    public ClientRuntime(ITunnelController engine) : this(engine, new ClientTunnelHost()) { }
    internal ClientRuntime(ITunnelController engine, IClientTunnelHost host, ClientPathHealth? path = null)
    {
        Engine = engine;
        Persistence = new(this);
        Proxy = new(this);
        Updates = new(this);
        Preferences.PropertyChanged += PreferencesChanged;
        tunnelHost = host; TunnelPolicy = new(this); Tunnels = new(this, host);
        Monitor = new ClientHealthMonitor(this, path: path);
        Recovery = new ClientRecovery(this);
        previousLookup = ServerTweak.Lookup; lookup = Profiles.TweakFor;
        ServerTweak.Lookup = lookup;
    }

    public async Task<SystemTunnelSession> CreateTunnelAsync(CancellationToken token)
    {
        await StopTunnelAsync().ConfigureAwait(true);
        TunnelLink = await tunnelHost.ConnectAsync(token).ConfigureAwait(true);
        Helper = TunnelLink.Lifetime;
        Tunnel = new SystemTunnelSession(Engine, TunnelLink.Client, ownsResources: false);
        Tunnel.DirectAllowed = AllowsDirect;
        Tunnel.Changed += TunnelStateChanged;
        Tunnel.ProtectionRepaired += TunnelProtectionRepaired;
        return Tunnel;
    }
    private bool AllowsDirect() => Engine is Engine core && (core.DirectEntry is null || RoutingLists is not null) && !core.Fragment;
    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => Persistence.Queue();
    private void TunnelStateChanged(SystemTunnelState state)
    {
        TunnelChanged?.Invoke(state);
        if (state is SystemTunnelState.Failed or SystemTunnelState.RecoveryRequired)
            Monitor.Dispatch(() => _ = Recovery.HealLostTunnelAsync());
    }
    private void TunnelProtectionRepaired() => ProtectionRepaired?.Invoke();

    public Task StopTunnelAsync()
    {
        if (Tunnel is null && TunnelLink is null) return stopping.IsCompleted ? Task.CompletedTask : stopping;
        var session = Tunnel; var link = TunnelLink;
        Tunnel = null; Helper = null; TunnelLink = null; RoutingLists = null;
        return stopping = ReleaseAsync(session, link);
    }

    private static async Task ReleaseAsync(SystemTunnelSession? session, IClientTunnelLink? link)
    {
        try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); }
        finally { if (link is not null) await link.DisposeAsync().ConfigureAwait(false); }
    }

    // При завершении сеанса Windows ожидание IPC выполняет служба; здесь нет синхронной блокировки UI.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Preferences.PropertyChanged -= PreferencesChanged;
        Recovery.Dispose();
        Updates.Dispose();
        Persistence.Dispose();
        Tray?.Dispose(); Tray = null;
        Monitor.Dispose();
        Proxy.Dispose();
        TunnelChanged = null; ProtectionRepaired = null;
        if (ServerTweak.Lookup == lookup) ServerTweak.Lookup = previousLookup;
        Engine.Dispose(); Profiles.Clear();
    }
}

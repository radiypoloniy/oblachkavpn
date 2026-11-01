namespace Oblachka;

// Сетевые ресурсы приложения не принадлежат WPF-окну. Штатное закрытие сначала снимает туннель.
public sealed class ClientRuntime : IDisposable
{
    public ITunnelController Engine { get; }
    public ClientProfiles Profiles { get; } = new();
    public ClientPreferences Preferences { get; } = new();
    public ClientActivity Activity { get; } = new();
    public ClientHealthMonitor Monitor { get; }
    public SystemTunnelSession? Tunnel { get; private set; }
    public IHelperLifetime? Helper { get; private set; }
    private Task stopping = Task.CompletedTask;
    private readonly Func<Profile, ServerTweak?> previousLookup, lookup;
    private bool disposed;

    public ClientRuntime() : this(new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"),
        Path.Combine(AppContext.BaseDirectory, "data", "runtime"))) { }
    public ClientRuntime(ITunnelController engine)
    {
        Engine = engine;
        Monitor = new ClientHealthMonitor(this);
        previousLookup = ServerTweak.Lookup; lookup = Profiles.TweakFor;
        ServerTweak.Lookup = lookup;
    }

    public async Task<SystemTunnelSession> CreateTunnelAsync(CancellationToken token)
    {
        await StopTunnelAsync().ConfigureAwait(true);
        Helper = await NetworkHelperConnection.ConnectAsync(token).ConfigureAwait(true);
        Tunnel = new SystemTunnelSession(Engine, Helper.Client, ownsResources: false);
        return Tunnel;
    }

    public Task StopTunnelAsync()
    {
        if (Tunnel is null && Helper is null) return stopping.IsCompleted ? Task.CompletedTask : stopping;
        var session = Tunnel; var lifetime = Helper;
        Tunnel = null; Helper = null;
        return stopping = ReleaseAsync(session, lifetime);
    }

    private static async Task ReleaseAsync(SystemTunnelSession? session, IHelperLifetime? lifetime)
    {
        try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); }
        finally { if (lifetime is not null) await lifetime.DisposeAsync().ConfigureAwait(false); }
    }

    // При завершении сеанса Windows ожидание IPC выполняет служба; здесь нет синхронной блокировки UI.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Monitor.Dispose();
        if (ServerTweak.Lookup == lookup) ServerTweak.Lookup = previousLookup;
        Engine.Dispose(); Profiles.Clear();
    }
}

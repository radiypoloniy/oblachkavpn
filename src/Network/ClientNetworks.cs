namespace Oblachka;

internal interface IClientNetworkEnvironment
{
    (string Key, string Name, bool Public)? Read();
    bool Moved(SystemTunnelSession session);
}
internal sealed class ClientNetworkEnvironment : IClientNetworkEnvironment
{
    public (string Key, string Name, bool Public)? Read() => NetworkCost.Network() is { } n ? (NetworkPolicy.Key(n.Id), NetworkPolicy.CleanName(n.Name), n.Public) : null;
    public bool Moved(SystemTunnelSession session) => session.NetworkMoved();
}

// Сеть и выбор человека переживают закрытие окна. Все решения выполняются в контексте владельца.
public sealed partial class ClientNetworks : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly IClientNetworkEnvironment environment;
    private bool disposed, processing, policyPending;
    private int epoch;
    public (string Key, string Name, bool Public)? Current { get; internal set; }
    public string? StoppedOnNetwork { get; set; }
    public string Message { get; private set; } = "";
    public event Action? Changed;
    public event Action<string>? Securing;
    public event Action? Connected;
    internal ClientNetworks(ClientRuntime owner, IClientNetworkEnvironment? environment = null)
    { this.owner = owner; this.environment = environment ?? new ClientNetworkEnvironment(); }
    public void Read()
    {
        if (disposed) return;
        var next = environment.Read();
        if (next?.Key != Current?.Key)
        { epoch++; policyPending = true; owner.Measurements.InvalidateNetwork(); }
        Current = next; owner.Selection.NetworkKey = next?.Key;
        if (!disposed) Changed?.Invoke();
    }
    public async Task ProcessAsync()
    {
        if (disposed) return;
        if (processing) { Schedule(TimeSpan.FromSeconds(1)); return; }
        processing = true;
        try
        {
            Read();
            if (owner.Activity.Operation is not null || owner.Activity.RecoveryWait is not null || owner.Connections.IsActive || owner.Connections.IsStopping)
            { Schedule(TimeSpan.FromSeconds(1)); return; }
            var expected = epoch;
            if (policyPending) { await ApplyRuleSetAsync(); if (disposed || expected != epoch) return; policyPending = false; }
            await FollowAsync(); if (disposed || expected != epoch) return;
            await ApplyAsync();
        }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
        { Message = "Сеть изменилась, но применить настройки не удалось. Проверьте подключение."; if (!disposed) Changed?.Invoke(); }
        finally { processing = false; }
    }
    public async Task ApplyRuleSetAsync()
    {
        if (disposed || owner.Activity.VmTestActive || Current is not { } net ||
            !owner.Workspace.KnownNetworks.TryGetValue(net.Key, out var known) || known.RuleSet is not { } name) return;
        try
        {
            if (await owner.Rules.ApplySetAsync(name))
            { Message = $"Сеть «{net.Name}»: включён набор правил «{name}»."; Changed?.Invoke(); }
        }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
        { Message = "Применить набор правил сети не удалось. Проверьте настройки VPN."; Changed?.Invoke(); }
    }
    private bool ShouldConnect()
    {
        if (disposed || owner.Activity.VmTestActive || Current is not { } net || owner.Profiles.Selected is null ||
            owner.Connections.IsActive || owner.Connections.IsStopping) return false;
        var known = owner.Workspace.KnownNetworks.TryGetValue(net.Key, out var choice);
        return NetworkPolicy.ShouldConnect(choice?.Mode ?? NetworkMode.Usual, known, net.Public, owner.Preferences.SecureUnknown,
            owner.Engine.State is not ConnectionState.Idle || owner.Tunnel is not null || owner.Activity.Operation is not null || owner.Activity.RecoveryWait is not null,
            owner.Pause.Until is not null, StoppedOnNetwork == net.Key);
    }
    public async Task ApplyAsync()
    {
        if (!ShouldConnect()) return;
        var expected = epoch; var net = Current!.Value;
        await owner.Measurements.WaitForPingsAsync();
        if (!ShouldConnect() || expected != epoch) return;
        var target = owner.Selection.BeforeConnect(); if (target is null) return;
        var why = owner.Workspace.KnownNetworks.ContainsKey(net.Key) ? "в ней всегда VPN" : "это незнакомая общественная сеть";
        Message = $"Сеть «{net.Name}»: {why} — включаем.";
        Securing?.Invoke($"Сеть «{net.Name}»: {why}."); Changed?.Invoke();
        try { await owner.Connections.StartAsync(target); if (!disposed) Connected?.Invoke(); }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
        { Message = "Автоматическое подключение в этой сети не удалось. Попробуйте включить VPN вручную."; if (!disposed) Changed?.Invoke(); }
    }
    public async Task FollowAsync()
    {
        if (disposed || owner.Activity.VmTestActive || owner.Activity.Operation is not null || owner.Activity.RecoveryWait is not null ||
            owner.Connections.IsActive || owner.Connections.IsStopping || owner.Tunnel is not { } session ||
            owner.Profiles.Selected is not { } target || !environment.Moved(session)) return;
        await owner.Connections.StartAsync(target);
    }
    public void Dispose() { if (disposed) return; disposed = true; StopSchedule(); Changed = null; Securing = null; Connected = null; }
}

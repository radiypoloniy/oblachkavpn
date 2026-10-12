namespace RoutePilot;

public enum SystemTunnelState { Idle, Starting, Running, Stopping, RecoveryRequired, Failed }

// Async boundary for the future system mode. It deliberately does not implement synchronous Stop.
public sealed class SystemTunnelSession : IAsyncDisposable
{
    private readonly ITunnelController core;
    private readonly INetworkHelperClient helper;
    private readonly SemaphoreSlim operations = new(1);
    public SystemTunnelState State { get; private set; }
    public event Action<SystemTunnelState>? Changed;

    public SystemTunnelSession(ITunnelController core, INetworkHelperClient helper)
    {
        this.core = core; this.helper = helper;
        helper.ConnectionLost += OnLost;
        core.Changed += CoreChanged;
    }

    private void CoreChanged(ConnectionState state, string message) { if (state == ConnectionState.Failed) OnLost(); }
    private void OnLost() => _ = LostAsync();
    private async Task LostAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is not (SystemTunnelState.Running or SystemTunnelState.Starting)) return;
            try { await CleanupAsync().ConfigureAwait(false); }
            catch { Set(SystemTunnelState.RecoveryRequired); }
            if (State == SystemTunnelState.Idle) Set(SystemTunnelState.Failed);
        }
        finally { operations.Release(); }
    }

    public async Task StartAsync(TunnelEndpointPlan plan, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (State != SystemTunnelState.Idle) throw new UserError("Сначала остановите системный туннель.");
            Set(SystemTunnelState.Starting);
            try
            {
                await core.StartAsync(plan.PinnedProfile, token).ConfigureAwait(false);
                await helper.AcquireAsync(new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, core.Port, plan.Peers), token).ConfigureAwait(false);
                Set(SystemTunnelState.Running);
            }
            catch
            {
                try { await CleanupAsync().ConfigureAwait(false); }
                catch { Set(SystemTunnelState.RecoveryRequired); }
                throw;
            }
        }
        finally { operations.Release(); }
    }

    public async Task StopAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try { await CleanupAsync().ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    private async Task CleanupAsync()
    {
        Set(SystemTunnelState.Stopping);
        var recovered = false;
        try
        {
            var state = await helper.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            recovered = state == NetworkSessionState.Released;
        }
        catch { Set(SystemTunnelState.RecoveryRequired); throw; }
        finally { core.Stop(); }
        Set(recovered ? SystemTunnelState.Idle : SystemTunnelState.RecoveryRequired);
    }

    private void Set(SystemTunnelState state) { State = state; Changed?.Invoke(state); }

    public async ValueTask DisposeAsync()
    {
        helper.ConnectionLost -= OnLost; core.Changed -= CoreChanged;
        try { await StopAsync().ConfigureAwait(false); }
        finally { core.Dispose(); await helper.DisposeAsync().ConfigureAwait(false); }
    }
}

using System.Diagnostics;

namespace Oblachka;

// Один канал на конкретный процесс окна. Потеря окна закрывает только IPC, не ClientRuntime.
public sealed class ClientViewServer(ClientRuntime owner, SynchronizationContext context) : IDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;
    public string PipeName { get; } = HelperPipeIdentity.NewPipeName();

    public async Task ServeAsync(Process view, CancellationToken token)
    {
        _ = view.SafeHandle; // Удерживаем исходный процесс до ожидания подключения, а не только его PID.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        using var pipe = HelperPipeIdentity.CreateServer(PipeName);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        { connect.CancelAfter(TimeSpan.FromSeconds(15)); await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false); }
        HelperPipeIdentity.Client(pipe, view);
        var recent = new HashSet<Guid>(); var order = new Queue<Guid>();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                var request = await HelperProtocol.ReadAsync<ClientViewRequest>(pipe, idle.Token).ConfigureAwait(false);
                ClientViewReply reply;
                try
                {
                    ClientViewProtocol.Validate(request);
                    if (!recent.Add(request.RequestId)) throw new UserError("Повторная команда интерфейса.");
                    order.Enqueue(request.RequestId); if (order.Count > 64) recent.Remove(order.Dequeue());
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(60));
                    reply = await DispatchAsync(request, deadline.Token).ConfigureAwait(false);
                }
                catch (UserError) { reply = new(ClientViewProtocol.Version, request.RequestId, ClientViewFailure.InvalidRequest, null); }
                using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                write.CancelAfter(TimeSpan.FromSeconds(5));
                await HelperProtocol.WriteAsync(pipe, reply, write.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is EndOfStreamException or IOException or OperationCanceledException or UserError) { }
    }
    private Task<ClientViewReply> DispatchAsync(ClientViewRequest request, CancellationToken token)
    {
        var result = new TaskCompletionSource<ClientViewReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(async _ =>
        {
            if (token.IsCancellationRequested) { result.TrySetCanceled(token); return; }
            try { result.TrySetResult(await HandleAsync(request)); }
            catch (Exception error) when (error is UserError or IOException or OperationCanceledException or ObjectDisposedException or TimeoutException)
            { result.TrySetResult(new(ClientViewProtocol.Version, request.RequestId, ClientViewFailure.OperationFailed, null)); }
            catch (Exception error) { result.TrySetException(error); }
        }, null);
        return result.Task.WaitAsync(token);
    }
    private async Task<ClientViewReply> HandleAsync(ClientViewRequest request)
    {
        ClientViewReply Fail(ClientViewFailure failure) => new(ClientViewProtocol.Version, request.RequestId, failure, null);
        if (request.InstanceId != Guid.Empty && request.InstanceId != owner.InstanceId) return Fail(ClientViewFailure.OwnerChanged);
        var activity = owner.Activity;
        if (request.Operation == ClientViewOperation.Probe)
        {
            if (activity.Operation is not null || activity.RecoveryWait is not null || owner.Connections.IsActive || owner.Connections.IsStopping) return Fail(ClientViewFailure.Busy);
            if (owner.Engine.ProcessId is null || activity.ManualStop) return Fail(ClientViewFailure.NotConnected);
            owner.Monitor.NextCheck = DateTimeOffset.MinValue;
            _ = ObserveProbeAsync(owner.Monitor.PollAsync());
        }
        else if (request.Operation == ClientViewOperation.Stop) await owner.Connections.StopAsync();
        else if (request.Operation is ClientViewOperation.SelectServer or ClientViewOperation.Connect)
        {
            var target = request.ServerId is { } id ? ClientServerCatalog.Find(owner, id) : owner.Profiles.Selected;
            if (target is null) return Fail(ClientViewFailure.InvalidRequest);
            if (owner.Connections.IsStopping || activity.Operation is not null && !activity.BackgroundCheck && !owner.Connections.IsActive)
                return Fail(ClientViewFailure.Busy);
            var connects = request.Operation == ClientViewOperation.Connect || owner.Connections.IsActive || owner.Engine.State != ConnectionState.Idle || owner.Tunnel is not null;
            if (connects && target.IsFree && owner.Workspace.FreeConsent != FreeServers.ConsentVersion) return Fail(ClientViewFailure.ConsentRequired);
            if (connects && owner.Preferences.SystemTunnel && owner.Tunnel is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) } tunnel &&
                !tunnel.CanSwitchTo(target) && !request.AllowReconnect) return Fail(ClientViewFailure.RequiresReconnect);
            _ = ObserveProbeAsync(request.Operation == ClientViewOperation.Connect
                ? owner.Connections.StartAsync(target, request.AllowReconnect) : owner.Connections.SelectAsync(target, request.AllowReconnect));
        }
        var name = (owner.Profiles.Running ?? owner.Profiles.Selected)?.DisplayName;
        if (name?.Length > 256) name = name[..256];
        return new(ClientViewProtocol.Version, request.RequestId, ClientViewFailure.None, new(owner.InstanceId, owner.Engine.State,
            owner.Tunnel?.State, activity.Operation is not null || activity.RecoveryWait is not null || owner.Connections.IsActive || owner.Connections.IsStopping,
            activity.ManualStop, name, owner.Profiles.Imported.Count, owner.Engine.Port,
            owner.Engine.ProcessId is null || !owner.Monitor.VerifiedSinceStart ? null : owner.Monitor.LastProbe?.Success,
            ClientServerCatalog.Id(owner.Profiles.Selected), ClientServerCatalog.Id(owner.Profiles.Running), owner.Connections.LastResult),
            request.Operation == ClientViewOperation.ListServers ? ClientServerCatalog.Page(owner, request.Offset) : null);
    }
    public void Dispose()
    { if (Interlocked.Exchange(ref disposed, 1) == 0) { shutdown.Cancel(); shutdown.Dispose(); } }
    private static async Task ObserveProbeAsync(Task checking)
    {
        try { await checking; }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or ObjectDisposedException or TimeoutException) { }
    }
}

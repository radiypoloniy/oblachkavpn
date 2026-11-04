namespace Oblachka;

public enum ClientConnectionResult { None, Connected, Cancelled, Failed }

// Запуск принадлежит приложению. Последний явно запрошенный сервер заменяет очередь переключений.
public sealed partial class ClientConnections(ClientRuntime owner) : IDisposable
{
    private CancellationTokenSource? active;
    private sealed record StartRequest(Profile Target, bool AllowReconnect, bool RebuildTunnel);
    private StartRequest? requested;
    private bool disposed;
    public Task? Pending { get; private set; }
    public Task? Stopping { get; private set; }
    public bool IsActive => Pending is { IsCompleted: false } || active is not null;
    public bool IsStopping => Stopping is { IsCompleted: false };
    public ClientConnectionResult LastResult { get; private set; }
    public event Action? Changed;

    public Task StartAsync(Profile target, bool allowReconnect = false) =>
        StartAsync(target, allowReconnect, rebuildTunnel: false);

    internal Task RestartAsync(Profile target, bool rebuildTunnel) =>
        StartAsync(target, allowReconnect: rebuildTunnel, rebuildTunnel: rebuildTunnel);

    private Task StartAsync(Profile target, bool allowReconnect, bool rebuildTunnel)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsStopping || active?.IsCancellationRequested == true) throw new UserError("Подключение ещё останавливается.");
        Validate(target, allowReconnect);
        if (!IsActive && owner.Activity.Operation is not null && !owner.Activity.BackgroundCheck)
            throw new UserError("Дождитесь завершения текущей операции.");
        owner.Pause.Cancel(rememberDisconnected: false);
        owner.Networks.StoppedOnNetwork = null;
        if (owner.Preferences.SystemTunnel) owner.Measurements.PrepareTunnel();
        requested = new(target, allowReconnect, rebuildTunnel);
        owner.Profiles.Selected = target; owner.Persistence.Queue();
        if (IsActive) { Changed?.Invoke(); return Pending!; }
        owner.Activity.ManualStop = false; owner.Activity.RecoveryWait?.Cancel();
        owner.Monitor.Recovery.Reset(); owner.Monitor.Health.Reset(); owner.Monitor.Path.Reset();
        var checking = owner.Activity.BackgroundCheck ? owner.Monitor.PendingCheck : null;
        if (checking is not null) owner.Activity.Operation?.Cancel();
        active = new CancellationTokenSource();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending = completed.Task;
        _ = CompleteAsync(RunAsync(active, checking), completed);
        _ = ObserveAsync(Pending);
        return Pending;
    }
    public Task SelectAsync(Profile target, bool allowReconnect = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsActive && owner.Engine.State is ConnectionState.Running or ConnectionState.Verified && owner.Profiles.Running is { } running &&
            Profiles.Fingerprint(running) == Profiles.Fingerprint(target))
        { owner.Profiles.Selected = target; owner.Persistence.Queue(); Changed?.Invoke(); return Task.CompletedTask; }
        if (IsActive || owner.Engine.State is not ConnectionState.Idle || owner.Tunnel is not null)
            return StartAsync(target, allowReconnect);
        owner.Profiles.Selected = target; owner.Persistence.Queue(); Changed?.Invoke();
        return Task.CompletedTask;
    }
    private void Validate(Profile target, bool allowReconnect)
    {
        if (owner.Profiles.Imported.Count > 0 && !owner.Profiles.Imported.Any(p => ProfileSelection.Identity(p) == ProfileSelection.Identity(target)))
            throw new UserError("Сервер больше не входит в текущий список.");
        if (target.IsFree && owner.Workspace.FreeConsent != FreeServers.ConsentVersion)
            throw new UserError("Сначала подтвердите условия бесплатных серверов в приложении.");
        if (owner.Preferences.SystemTunnel && owner.Tunnel is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) } session &&
            !session.CanSwitchTo(target) && !allowReconnect)
            throw new UserError("Для этого сервера требуется переподключение со снятием защиты.");
    }
    private async Task RunAsync(CancellationTokenSource cancel, Task? checking)
    {
        LastResult = ClientConnectionResult.None;
        try
        {
            if (checking is not null) await ObserveAsync(checking);
            cancel.Token.ThrowIfCancellationRequested();
            if (owner.Activity.Operation is not null) throw new UserError("Дождитесь завершения текущей операции.");
            owner.Activity.Operation = cancel; owner.Activity.EngineOperation = true; Changed?.Invoke();
            while (requested is { } request)
            {
                requested = null;
                var target = request.Target;
                Validate(target, request.AllowReconnect); cancel.Token.ThrowIfCancellationRequested();
                var previousTunnel = owner.Tunnel;
                await StartOneAsync(request, cancel.Token);
                if (!ReferenceEquals(previousTunnel, owner.Tunnel)) owner.Rules.Remember();
                cancel.Token.ThrowIfCancellationRequested();
                owner.Profiles.Running = target; owner.Workspace.WasConnected = true;
                owner.Monitor.ObserveProcess(); owner.Monitor.StartedFresh();
                owner.Proxy.Sync(); owner.Persistence.Queue(); LastResult = ClientConnectionResult.Connected;
                Changed?.Invoke();
                if (requested is { RebuildTunnel: false } next && Profiles.Fingerprint(next.Target) == Profiles.Fingerprint(target)) requested = null;
            }
            cancel.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { LastResult = ClientConnectionResult.Cancelled; throw; }
        catch { LastResult = ClientConnectionResult.Failed; throw; }
        finally
        {
            requested = null;
            if (cancel.IsCancellationRequested)
            {
                try { await owner.StopTunnelAsync(); } catch (Exception error) when (error is UserError or IOException or TimeoutException or ObjectDisposedException) { }
                owner.Engine.Stop(); owner.Profiles.Running = null;
            }
            if (ReferenceEquals(owner.Activity.Operation, cancel)) { owner.Activity.Operation = null; owner.Activity.EngineOperation = false; }
            active = null; cancel.Dispose(); owner.Proxy.Sync();
        }
    }
    public Task StopAsync() => StopAsync(preservePause: false);
    internal Task StopAsync(bool preservePause)
    {
        if (!preservePause) owner.Pause.Cancel();
        if (IsStopping) return Stopping!;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stopping = completed.Task;
        _ = CompleteAsync(StopCoreAsync(), completed);
        _ = ObserveAsync(Stopping);
        return Stopping;
    }
    private async Task StopCoreAsync()
    {
        requested = null; owner.Activity.ManualStop = true; owner.Activity.RecoveryWait?.Cancel();
        owner.Networks.StoppedOnNetwork = owner.Networks.Current?.Key;
        var checking = owner.Activity.BackgroundCheck ? owner.Monitor.PendingCheck : null;
        owner.Activity.Operation?.Cancel(); active?.Cancel();
        owner.Workspace.WasConnected = false;
        owner.Persistence.Queue(); Changed?.Invoke();
        if (Pending is { IsCompleted: false } starting) await ObserveAsync(starting);
        if (checking is not null) await ObserveAsync(checking);
        await owner.StopTunnelAsync();
        if (owner.Engine.State is not ConnectionState.Idle && (owner.Activity.Operation is null || !owner.Activity.EngineOperation)) owner.Engine.Stop();
        if (owner.Engine.State == ConnectionState.Idle) owner.Profiles.Running = null;
        owner.Proxy.Sync(); owner.Persistence.Queue(); Changed?.Invoke();
    }
    private static async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception error) when (error is UserError or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }
    }
    private async Task CompleteAsync(Task task, TaskCompletionSource completed)
    {
        try { await task; completed.TrySetResult(); }
        catch (OperationCanceledException) { completed.TrySetCanceled(); }
        catch (Exception error) { completed.TrySetException(error); }
        finally { if (!disposed) Changed?.Invoke(); }
    }
    public void Dispose() { if (disposed) return; disposed = true; requested = null; active?.Cancel(); Changed = null; }
}

namespace Oblachka;

public sealed partial class ClientRecovery
{
    private readonly Queue<DateTimeOffset> heals = new();
    private int healing;
    private sealed record TunnelHeal(Profile Target, bool MainPaused, DateTimeOffset Until);
    private TunnelHeal? pendingTunnelHeal;
    internal bool HasPendingTunnelHeal => pendingTunnelHeal is not null;
    internal void CancelTunnelHeal() => pendingTunnelHeal = null;
    private static string RecoveryMessage(Exception error) => error is UserError ? error.Message : "Связь со службой прервалась. Подключитесь снова или восстановите сеть.";
    private async Task RebuildOrRequestAsync(Profile previous, Profile selected, CancellationToken token, bool mainWasPaused = false)
    {
        if (!owner.Tunnels.CanRebuild)
        { Finish(new(RecoveryOutcomeKind.RequiresReconnect, previous, selected, [])); return; }
        Message = "Восстанавливаем системный туннель."; StateChanged?.Invoke();
        mainWasPaused |= owner.Connections.MainPaused;
        try { await owner.Tunnels.StartAsync(selected, token, preserveMainPause: true, recoverAppliedPins: true, mainWasPaused: mainWasPaused); }
        catch (Exception error) when (!token.IsCancellationRequested && error is UserError or IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
            if (!disposed && owner.Preferences.SystemTunnel && owner.Tunnel is null && pendingTunnelHeal is null &&
                (!owner.Activity.ManualStop || mainWasPaused))
            {
                var now = DateTimeOffset.UtcNow;
                pendingTunnelHeal = new(selected, mainWasPaused, now.AddMinutes(2));
                owner.Monitor.NextCheck = now.AddSeconds(3);
            }
            throw;
        }
        token.ThrowIfCancellationRequested();
        owner.Profiles.Selected = selected;
        Finish(new(RecoveryOutcomeKind.Rebuilt, previous, selected, []));
    }
    public async Task RepairPathAsync(string problem)
    {
        var activity = owner.Activity; var selected = owner.Profiles.Selected; var path = owner.Monitor.Path;
        if (disposed || activity.ManualStop || activity.VmTestActive || activity.Operation is not null || selected is null || !owner.Tunnels.CanRebuild || owner.Tunnel is null) return;
        if (activity.RecoveryWait is { } retry)
        { retry.Cancel(); owner.Monitor.NextCheck = DateTimeOffset.UtcNow.AddSeconds(1); return; }
        var before = LastOutcome; path.Problem = problem;
        if (path.Rebuilt)
        {
            var first = !path.Broken;
            path.Broken = true; NetworkClosed = true; owner.Monitor.NextCheck = DateTimeOffset.UtcNow.AddSeconds(30);
            if (!first) return;
            Finish(new(RecoveryOutcomeKind.PathBroken, selected, null, [], Message: problem + " Переподключение не помогло. Выключите VPN, чтобы открыть обычный интернет."));
            Publish(before); return;
        }
        using var cancel = new CancellationTokenSource(); active = cancel;
        activity.Operation = cancel; activity.EngineOperation = true;
        path.Rebuilt = true;
        try
        {
            StateChanged?.Invoke(); await RebuildOrRequestAsync(selected, selected, cancel.Token);
            path.Tunnel.Reset(); path.Direct.Reset();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) when (error is UserError or IOException or TimeoutException or ObjectDisposedException)
        { Finish(new(RecoveryOutcomeKind.Error, selected, null, [], Message: RecoveryMessage(error))); }
        finally
        {
            active = null;
            if (ReferenceEquals(activity.Operation, cancel)) { activity.Operation = null; activity.EngineOperation = false; }
            if (!disposed) StateChanged?.Invoke(); Publish(before);
        }
    }
    internal void PathHealthy()
    { owner.Monitor.Path.Rebuilt = owner.Monitor.Path.Broken = false; NetworkClosed = false; }

    internal void QueueTunnelHeal() => QueueTunnelHeal(afterSleep: false);

    internal void ResumeTunnelHeal() => QueueTunnelHeal(afterSleep: true);

    private void QueueTunnelHeal(bool afterSleep)
    {
        if (disposed || !owner.Preferences.SystemTunnel || !owner.Tunnels.CanRebuild || owner.Profiles.Running is not { } target) return;
        var until = DateTimeOffset.UtcNow.AddMinutes(2);
        if (pendingTunnelHeal is { } pending)
        {
            if (!afterSleep || Volatile.Read(ref healing) != 0) return;
            pendingTunnelHeal = pending with { Until = until };
        }
        else
        {
            var lost = owner.Tunnel;
            if (lost is not { State: SystemTunnelState.Failed or SystemTunnelState.RecoveryRequired } &&
                !(afterSleep && lost is null && owner.Workspace.WasConnected && owner.Pause.Until is null)) return;
            var mainPaused = lost?.MainPaused == true;
            if (owner.Activity.ManualStop && !mainPaused) return;
            pendingTunnelHeal = new(target, mainPaused, until);
        }
        owner.Monitor.NextCheck = DateTimeOffset.UtcNow;
    }

    internal async Task HealLostTunnelAsync()
    {
        var activity = owner.Activity;
        if (disposed || pendingTunnelHeal is not { } request || Volatile.Read(ref healing) != 0) return;
        var now = DateTimeOffset.UtcNow;
        if (!owner.Preferences.SystemTunnel || activity.ManualStop && !request.MainPaused ||
            owner.Tunnel is { State: SystemTunnelState.Running } || now >= request.Until)
        { CancelTunnelHeal(); return; }
        if (activity.VmTestActive || activity.Operation is not null || activity.RecoveryWait is not null ||
            owner.Connections.IsStopping || !owner.ServiceAvailable)
        { owner.Monitor.NextCheck = now.AddSeconds(2); return; }
        if (Interlocked.CompareExchange(ref healing, 1, 0) != 0) return;
        while (heals.Count > 0 && now - heals.Peek() > TimeSpan.FromMinutes(10)) heals.Dequeue();
        if (heals.Count >= 3) { CancelTunnelHeal(); Interlocked.Exchange(ref healing, 0); return; }
        var before = LastOutcome;
        var selected = request.Target;
        using var cancel = new CancellationTokenSource(); active = cancel;
        activity.Operation = cancel; activity.EngineOperation = true;
        try
        {
            await wait(TimeSpan.FromSeconds(2), cancel.Token);
            if (!ReferenceEquals(pendingTunnelHeal, request) || !owner.Preferences.SystemTunnel ||
                activity.ManualStop && !request.MainPaused) return;
            if (!owner.ServiceAvailable)
            { owner.Monitor.NextCheck = DateTimeOffset.UtcNow.AddSeconds(2); return; }
            heals.Enqueue(DateTimeOffset.UtcNow);
            await RebuildOrRequestAsync(selected, selected, cancel.Token, request.MainPaused);
            CancelTunnelHeal();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) when (error is UserError or IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
            if (heals.Count >= 3) CancelTunnelHeal();
            else if (ReferenceEquals(pendingTunnelHeal, request)) owner.Monitor.NextCheck = DateTimeOffset.UtcNow.AddSeconds(3);
            Finish(new(RecoveryOutcomeKind.Error, selected, null, [], Message: RecoveryMessage(error)));
        }
        finally
        {
            active = null;
            if (ReferenceEquals(activity.Operation, cancel)) { activity.Operation = null; activity.EngineOperation = false; }
            Interlocked.Exchange(ref healing, 0);
            if (!disposed) StateChanged?.Invoke(); Publish(before);
        }
    }
}

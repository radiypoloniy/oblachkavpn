namespace Oblachka;

public sealed partial class ClientRecovery
{
    private readonly Queue<DateTimeOffset> heals = new();
    private int healing;
    private static string RecoveryMessage(Exception error) => error is UserError ? error.Message : "Связь со службой прервалась. Подключитесь снова или восстановите сеть.";
    private async Task RebuildOrRequestAsync(Profile previous, Profile selected, CancellationToken token)
    {
        if (!owner.Tunnels.CanRebuild)
        { Finish(new(RecoveryOutcomeKind.RequiresReconnect, previous, selected, [])); return; }
        Message = "Пересоздаём системный туннель: на это время защита снимается."; StateChanged?.Invoke();
        await owner.Tunnels.StartAsync(selected, token);
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

    public async Task HealLostTunnelAsync()
    {
        var activity = owner.Activity;
        if (disposed || Volatile.Read(ref healing) != 0 || activity.ManualStop || activity.VmTestActive || activity.Operation is not null ||
            activity.RecoveryWait is not null || !owner.ServiceAvailable || !owner.Tunnels.CanRebuild || owner.Tunnel is null ||
            owner.Tunnel.State is not (SystemTunnelState.Failed or SystemTunnelState.RecoveryRequired) || owner.Profiles.Selected is not { } selected) return;
        if (Interlocked.CompareExchange(ref healing, 1, 0) != 0) return;
        var now = DateTimeOffset.UtcNow;
        while (heals.Count > 0 && now - heals.Peek() > TimeSpan.FromMinutes(10)) heals.Dequeue();
        if (heals.Count >= 3) { Interlocked.Exchange(ref healing, 0); return; }
        heals.Enqueue(now);
        var before = LastOutcome;
        using var cancel = new CancellationTokenSource(); active = cancel;
        activity.Operation = cancel; activity.EngineOperation = true;
        try
        {
            await wait(TimeSpan.FromSeconds(2), cancel.Token);
            if (activity.ManualStop) return;
            await RebuildOrRequestAsync(selected, selected, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) when (error is UserError or IOException or TimeoutException or ObjectDisposedException)
        { Finish(new(RecoveryOutcomeKind.Error, selected, null, [], Message: RecoveryMessage(error))); }
        finally
        {
            active = null;
            if (ReferenceEquals(activity.Operation, cancel)) { activity.Operation = null; activity.EngineOperation = false; }
            Interlocked.Exchange(ref healing, 0);
            if (!disposed) StateChanged?.Invoke(); Publish(before);
        }
    }
}

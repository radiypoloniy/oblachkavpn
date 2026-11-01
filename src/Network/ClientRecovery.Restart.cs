namespace Oblachka;

public sealed partial class ClientRecovery
{
    public async Task RestartFailedAsync()
    {
        var activity = owner.Activity;
        if (!Allowed || activity.Operation is not null || activity.RecoveryWait is not null || owner.Profiles.Selected is null) return;
        var before = LastOutcome;
        using var cancel = new CancellationTokenSource(); waiting = cancel; activity.RecoveryWait = cancel;
        try
        {
            while (Allowed && owner.Engine.State == ConnectionState.Failed)
            {
                var previous = owner.Profiles.Selected;
                var delay = owner.Monitor.Recovery.NextDelay(DateTimeOffset.UtcNow);
                if (delay is null)
                { Finish(new(RecoveryOutcomeKind.Exhausted, previous, null, [], Message: "После трёх попыток за две минуты восстановление приостановлено. Проверьте сеть или подключитесь вручную.")); return; }
                Message = $"Ядро остановилось. Повторный запуск через {delay.Value.TotalSeconds:0} с…"; StateChanged?.Invoke();
                await wait(delay.Value, cancel.Token);
                previous = owner.Profiles.Selected;
                if (!Allowed || activity.Operation is not null || owner.Engine.State != ConnectionState.Failed || previous is null) return;
                if (owner.Preferences.SystemTunnel && !TunnelActive)
                { Finish(new(RecoveryOutcomeKind.RequiresReconnect, previous, previous, [])); return; }
                activity.Operation = cancel; activity.EngineOperation = true;
                try
                {
                    owner.Engine.Stop();
                    if (TunnelActive) await tunnel()!.SwitchAsync(previous, cancel.Token);
                    else await owner.Engine.StartAsync(previous, cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                    if (!Allowed) return;
                    owner.Profiles.Running = previous; owner.Monitor.ObserveProcess(); owner.Monitor.StartedFresh();
                    Finish(new(RecoveryOutcomeKind.Restarted, previous, previous, []));
                }
                catch (UserError error) { Finish(new(RecoveryOutcomeKind.Error, previous, null, [], Message: error.Message)); }
                finally
                {
                    if (ReferenceEquals(activity.Operation, cancel)) { activity.Operation = null; activity.EngineOperation = false; }
                    if (!disposed) StateChanged?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally
        {
            waiting = null;
            if (ReferenceEquals(activity.RecoveryWait, cancel)) activity.RecoveryWait = null;
            Publish(before);
        }
    }
}

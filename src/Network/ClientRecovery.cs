namespace Oblachka;

public enum RecoveryOutcomeKind { Recovered, Restarted, NotFound, RequiresReconnect, Error, Exhausted, Rebuilt, PathBroken }
public sealed record RecoveryOutcome(RecoveryOutcomeKind Kind, Profile? Previous, Profile? Selected,
    IReadOnlyList<RouteAttempt> Attempts, bool Fragment = false, string? Message = null);

// Выполняет восстановление в текущей защите. Пересоздание TUN пока требует конфигурации от UI.
public sealed partial class ClientRecovery : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly Func<SystemTunnelSession?> tunnel;
    private readonly Func<Profile, CancellationToken, Task<Diagnosis>> diagnose;
    private readonly Func<TimeSpan, CancellationToken, Task> wait;
    private CancellationTokenSource? active, waiting;
    private bool disposed;
    public RecoveryOutcome? LastOutcome { get; private set; }
    public string Message { get; private set; } = "";
    public bool NetworkClosed { get; set; }
    public event Action? StateChanged;
    public event Action<RecoveryOutcome>? Completed;
    internal ClientRecovery(ClientRuntime owner, Func<SystemTunnelSession?>? tunnel = null,
        Func<Profile, CancellationToken, Task<Diagnosis>>? diagnose = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        this.owner = owner; this.tunnel = tunnel ?? (() => owner.Tunnel);
        this.diagnose = diagnose ?? ((profile, token) => ConnectionDiagnosis.RunAsync(profile, token)); this.wait = wait ?? Task.Delay;
    }
    private bool Allowed => !disposed && !owner.Activity.VmTestActive && !owner.Activity.ManualStop &&
        (owner.Profiles.AutoSelect || owner.Preferences.AutoRecover) &&
        (owner.Profiles.Running ?? owner.Profiles.Selected) is { IsFree: false };
    private bool TunnelActive => tunnel() is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) };

    public async Task RecoverAsync()
    {
        var activity = owner.Activity; var data = owner.Profiles; var previous = data.Selected;
        if (!Allowed || activity.Operation is not null || activity.RecoveryWait is not null || previous is null) return;
        var before = LastOutcome;
        var delay = owner.Monitor.Recovery.NextDelay(DateTimeOffset.UtcNow);
        if (delay is null)
        {
            owner.Monitor.NextCheck = DateTimeOffset.MaxValue;
            Finish(new(RecoveryOutcomeKind.Exhausted, previous, null, [], Message: "Автовосстановление исчерпало три попытки за две минуты. Подключитесь вручную."));
            Publish(before);
            return;
        }
        using var cancel = new CancellationTokenSource(); active = cancel;
        activity.Operation = cancel; activity.EngineOperation = activity.FindingRoute = true;
        try
        {
            StateChanged?.Invoke(); await wait(delay.Value, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (!Allowed) return;
            if (owner.Preferences.SystemTunnel && !TunnelActive)
            { await RebuildOrRequestAsync(previous, previous, cancel.Token); return; }
            var plan = BuildPlan(previous);
            var search = CreateSearch();
            var result = plan.Siblings.Length > 0
                ? await search.FindAsync(plan.Subset, plan.Siblings, TunnelHealth.Targets[0], cancel.Token) : null;
            if (result?.ProfileIndex is null && plan.Outside.Length > 0)
            { await RebuildOrRequestAsync(previous, plan.Subset[plan.Outside[0]], cancel.Token); return; }
            if (result?.ProfileIndex is null)
            {
                var diagnosis = await DiagnoseAsync(previous, cancel.Token); data.LastDiagnosis = diagnosis;
                var steps = RouteLadder.Plan(plan.Subset, plan.Order, previous, diagnosis.Kind, data.FragmentMode)
                    .Where(s => !plan.Siblings.Contains(s)).ToArray();
                var rest = await search.FindAsync(plan.Subset, steps, TunnelHealth.Targets[0], cancel.Token);
                result = rest with { Attempts = [.. result?.Attempts ?? [], .. rest.Attempts] };
            }
            cancel.Token.ThrowIfCancellationRequested();
            if (!Allowed) return;
            Apply(plan, previous, result);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) when (error is UserError or IOException or TimeoutException or ObjectDisposedException)
        { Finish(new(RecoveryOutcomeKind.Error, previous, null, [], Message: RecoveryMessage(error))); }
        finally
        {
            active = null;
            if (ReferenceEquals(activity.Operation, cancel))
            { activity.Operation = null; activity.EngineOperation = activity.FindingRoute = false; }
            if (!disposed) StateChanged?.Invoke();
            Publish(before);
        }
    }
    private void Apply(SearchPlan plan, Profile previous, RouteSearchResult result)
    {
        var data = owner.Profiles;
        foreach (var attempt in result.Attempts)
            data.MarkMeasured(plan.Subset[attempt.ProfileIndex], attempt.Outcome == "Verified" ? attempt.Milliseconds : null);
        var attempts = result.Attempts.Select(a => a with { ProfileIndex = plan.Candidates[a.ProfileIndex] }).ToArray();
        if (result.ProfileIndex is not { } found)
        { Finish(new(RecoveryOutcomeKind.NotFound, previous, null, attempts)); return; }
        var selected = plan.Subset[found]; data.Selected = data.Running = selected;
        owner.Monitor.ObserveProcess();
        owner.Monitor.Health.Reset(); owner.Monitor.VerifiedSinceStart = true;
        owner.Monitor.LastProbe = new(true, result.Attempts[^1].Milliseconds, "Соединение восстановлено");
        owner.Monitor.LastCheck = DateTimeOffset.UtcNow;
        if (data.FragmentMode == FragmentMode.Auto) data.FragmentLearned = result.Fragment;
        Finish(new(RecoveryOutcomeKind.Recovered, previous, selected, attempts, result.Fragment));
    }
    private void Finish(RecoveryOutcome result)
    {
        if (!disposed)
        {
            LastOutcome = result; Message = result.Message ?? "";
            if (result.Kind is RecoveryOutcomeKind.NotFound or RecoveryOutcomeKind.Exhausted) NetworkClosed = TunnelActive;
            else if (result.Kind is RecoveryOutcomeKind.Recovered or RecoveryOutcomeKind.Restarted or RecoveryOutcomeKind.Rebuilt) NetworkClosed = false;
        }
    }
    private void Publish(RecoveryOutcome? before)
    { if (!disposed && LastOutcome is { } result && !ReferenceEquals(before, result)) Completed?.Invoke(result); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        active?.Cancel(); waiting?.Cancel(); StateChanged = null; Completed = null; LastOutcome = null;
    }
}

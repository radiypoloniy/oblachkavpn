namespace Oblachka;

public sealed record HealthObservation(HealthRound Round, int FailureLimit, bool ShouldRecover, string? PathProblem);

// Самостоятельные проверки без WPF. Смена маршрута пока выполняется обработчиком результата в окне.
public sealed partial class ClientHealthMonitor : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly Func<DateTimeOffset> now;
    public TunnelHealth Health { get; } = new();
    public RecoveryPolicy Recovery { get; } = new();
    public ClientPathHealth Path { get; }
    private DateTimeOffset next = DateTimeOffset.MaxValue;
    public DateTimeOffset NextCheck { get => next; set { next = value; Wake(); } }
    public DateTimeOffset LastTraffic { get; set; } = DateTimeOffset.MinValue;
    public bool VerifiedSinceStart { get; set; }
    public ProbeResult? LastProbe { get; set; }
    public DateTimeOffset? LastCheck { get; set; }
    public HealthObservation? LastObservation { get; private set; }
    private int? observedPid;
    private int generation;
    private bool polling;
    private volatile bool disposed;
    private CancellationTokenSource? probing;
    public event Action? StateChanged;
    public event Action<HealthObservation>? Completed;

    internal ClientHealthMonitor(ClientRuntime owner, Func<DateTimeOffset>? now = null, ClientPathHealth? path = null)
    { this.owner = owner; this.now = now ?? (() => DateTimeOffset.UtcNow); Path = path ?? new(); }

    public void StartedFresh()
    {
        generation++; VerifiedSinceStart = false; LastObservation = null;
        NextCheck = now().AddSeconds(1);
    }
    public void ObserveProcess()
    {
        if (owner.Engine.ProcessId == observedPid) return;
        observedPid = owner.Engine.ProcessId;
        LastProbe = null; LastCheck = null;
        if (observedPid is not null) NextCheck = now().AddSeconds(3);
    }

    public async Task PollAsync()
    {
        var activity = owner.Activity;
        if (disposed || polling || activity.VmTestActive || activity.ManualStop || activity.Operation is not null || activity.RecoveryWait is not null ||
            owner.Engine.ProcessId is null || now() < next) return;
        polling = true;
        var version = generation;
        using var cancel = new CancellationTokenSource(); probing = cancel;
        activity.Operation = cancel; activity.EngineOperation = activity.BackgroundCheck = true;
        next = now().AddSeconds(now() - LastTraffic < TimeSpan.FromSeconds(60) ? 20 : 45);
        HealthObservation? observation = null;
        try
        {
            StateChanged?.Invoke();
            var fresh = !VerifiedSinceStart;
            var round = await Health.CheckAsync(owner.Engine.ProbeAsync, TunnelHealth.Targets, cancel.Token,
                fresh ? TimeSpan.FromSeconds(5) : null);
            if (disposed || activity.ManualStop || generation != version) return;
            if (round.Verified) VerifiedSinceStart = true;
            var profile = owner.Profiles.Selected;
            var sibling = owner.Profiles.MachineMode && profile is not null &&
                owner.Profiles.Imported.Count(p => ServerMachines.Key(p) == ServerMachines.Key(profile)) > 1;
            var limit = fresh ? (sibling ? 1 : 2) : owner.Profiles.AutoSelect ? 2 : 3;
            string? problem = null;
            if (!round.Reachable) next = now().AddSeconds(fresh ? 3 : 8);
            else
            {
                var active = owner.Tunnel is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) };
                problem = await Path.CheckAsync(active, owner.Preferences.RussiaDirect &&
                    owner.Engine is Engine { Routing.HasDirect: true }, cancel.Token);
                if (problem is not null && !Path.ShouldRepair) next = now().AddSeconds(10);
            }
            if (disposed || activity.ManualStop || generation != version) return;
            LastProbe = problem is null ? round.Results.FirstOrDefault(r => r.Success) ?? round.Results[^1]
                : new(false, 0, problem, ProbeFailure.Reachability);
            LastCheck = now();
            observation = LastObservation = new(round, limit, Health.FailedRounds >= limit, problem);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (UserError) { /* Выход ядра обрабатывается отдельным восстановлением. */ }
        finally
        {
            probing = null; polling = false;
            if (ReferenceEquals(activity.Operation, cancel))
            { activity.Operation = null; activity.EngineOperation = activity.BackgroundCheck = false; }
            if (!disposed) StateChanged?.Invoke();
        }
        if (observation is not null && !disposed && !activity.ManualStop) Completed?.Invoke(observation);
    }
}

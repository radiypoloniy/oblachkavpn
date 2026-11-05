using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private TunnelHealth health => runtime.Monitor.Health;
    private DateTimeOffset nextHealthCheck { get => runtime.Monitor.NextCheck; set => runtime.Monitor.NextCheck = value; }
    private bool verifiedSinceStart { get => runtime.Monitor.VerifiedSinceStart; set => runtime.Monitor.VerifiedSinceStart = value; }
    private bool backgroundWorkEnabled;
    private void StartedFresh() => runtime.Monitor.StartedFresh();
    private void SubscribeLifecycle(bool enabled)
    {
        if (!enabled || backgroundWorkEnabled) return;
        backgroundWorkEnabled = true;
        runtime.Monitor.StateChanged += HealthStateChanged;
        runtime.Monitor.Completed += HealthCompleted;
        runtime.ServiceRoutes.StatusChanged += ServiceRoutesStatusChanged;
        runtime.Recovery.StateChanged += RecoveryStateChanged;
        runtime.Recovery.Completed += RecoveryCompleted;
        runtime.Monitor.Start(new DispatcherSynchronizationContext(Dispatcher));
        runtime.Pause.Start(new DispatcherSynchronizationContext(Dispatcher));
        runtime.Imports.Start(new DispatcherSynchronizationContext(Dispatcher));
        runtime.Measurements.Start(new DispatcherSynchronizationContext(Dispatcher));
        runtime.Networks.Start(new DispatcherSynchronizationContext(Dispatcher));
        runtime.Traffic.Start(new DispatcherSynchronizationContext(Dispatcher));
        runtime.Zapret.Start(new DispatcherSynchronizationContext(Dispatcher));
        runtime.Tray?.StartNotices();
        runtime.Persistence.Start(new DispatcherSynchronizationContext(Dispatcher));
        if (stateStore is not null && !isolatedPersistence) runtime.Updates.Start(new DispatcherSynchronizationContext(Dispatcher));
    }
    private void UnsubscribeLifecycle()
    {
        runtime.Monitor.StateChanged -= HealthStateChanged;
        runtime.Monitor.Completed -= HealthCompleted;
        runtime.ServiceRoutes.StatusChanged -= ServiceRoutesStatusChanged;
        runtime.Recovery.StateChanged -= RecoveryStateChanged;
        runtime.Recovery.Completed -= RecoveryCompleted;
        backgroundWorkEnabled = false;
    }
    private void HealthStateChanged() { if (!closed) UpdateControls(); }
    private void ServiceRoutesStatusChanged() { if (!closed) RefreshServiceRoutes(); }
    private bool AutoSwitching => (autoSelect || preferences.AutoRecover) && (runningProfile ?? profile)?.IsFree != true;
    private async void HealthCompleted(HealthObservation observation)
    {
        if (closed || vmTestActive || manualStop) return;
        var round = observation.Round;
        RecordProbe(round.Results.FirstOrDefault(r => r.Success) ?? round.Results[^1]);
        if (!round.Reachable)
            DetailText.Text = $"Проверка связи не прошла: {health.FailedRounds} из {observation.FailureLimit}. Проверяем два независимых сайта.";
        else if (observation.PathProblem is { } problem)
        {
            RecordProbe(new(false, 0, problem, ProbeFailure.Reachability));
            DetailText.Text = problem + " Проверяем ещё раз.";
        }
        else SystemPathHealthy();
        if (observation.ShouldRecover && (runningProfile ?? profile)?.IsFree == true)
            DetailText.Text = "Бесплатный сервер не отвечает. Выберите другой и подключитесь заново — сам он не переключается.";
    }
}

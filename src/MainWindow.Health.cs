using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Windows;

namespace RoutePilot;

public partial class MainWindow
{
    private readonly TunnelHealth health = new();
    private DateTimeOffset nextHealthCheck = DateTimeOffset.MaxValue;
    private bool lifecycleSubscribed;
    private bool backgroundWorkEnabled;

    private void SubscribeLifecycle(bool enabled)
    {
        if (!enabled) return;
        backgroundWorkEnabled = true;
        SystemEvents.PowerModeChanged += PowerChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        lifecycleSubscribed = true;
    }
    private void UnsubscribeLifecycle()
    {
        if (!lifecycleSubscribed) return;
        SystemEvents.PowerModeChanged -= PowerChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    { if (args.Mode == PowerModes.Resume) QueueHealthCheck(); }
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    { if (args.IsAvailable) QueueHealthCheck(); }
    private void QueueHealthCheck()
    {
        if (closed || vmTestActive || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!closed) nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(3); }));
    }
    private async void PollHealth()
    {
        if (closed || vmTestActive || !backgroundWorkEnabled || operation is not null || recoveryWait is not null || engine.ProcessId is null ||
            AutoRecover.IsChecked != true || DateTimeOffset.UtcNow < nextHealthCheck) return;
        nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(45);
        operation = new CancellationTokenSource(); engineOperation = true; UpdateControls();
        var shouldRecover = false;
        try
        {
            var round = await health.CheckAsync(engine.ProbeAsync, TunnelHealth.Targets, operation.Token);
            if (!closed && !manualStop)
            {
                RecordProbe(round.Results.FirstOrDefault(r => r.Success) ?? round.Results[^1]);
                shouldRecover = health.ShouldRecover;
                if (!round.Reachable) DetailText.Text = $"Проверка связи не прошла: {health.FailedRounds} из 3. Проверяем два независимых сайта.";
            }
        }
        catch (OperationCanceledException) { }
        catch (UserError) { /* Engine exit has its own bounded recovery owner. */ }
        finally
        {
            operation.Dispose(); operation = null; engineOperation = false;
            if (manualStop) engine.Stop();
            if (!closed) UpdateControls();
        }
        if (closed || vmTestActive || manualStop || AutoRecover.IsChecked != true) return;
        if (shouldRecover) await RecoverUnreachableAsync();
        else if (engine.State == ConnectionState.Failed) ScheduleRecovery();
    }
    private async Task RecoverUnreachableAsync()
    {
        if (operation is not null || profile is null) return;
        var delay = recovery.NextDelay(DateTimeOffset.UtcNow);
        if (delay is null) { DetailText.Text = "Автовосстановление исчерпало три попытки за две минуты. Подключи вручную."; nextHealthCheck = DateTimeOffset.MaxValue; return; }
        operation = new CancellationTokenSource(); engineOperation = true; findingRoute = true; UpdateControls();
        try
        {
            await Task.Delay(delay.Value, operation.Token);
            var profiles = importedProfiles.ToArray();
            var selected = ProfileSelection.IndexOf(profiles, profile);
            var result = await new RouteSearch(engine).FindAsync(profiles, selected, TunnelHealth.Targets[0], operation.Token);
            routeAttempts = result.Attempts;
            if (result.ProfileIndex is int index && !closed)
            {
                Servers.SearchBox.Clear(); ProfilePicker.ItemsSource = importedProfiles;
                ProfilePicker.SelectedIndex = index; profile = profiles[index]; health.Reset();
                RecordProbe(new(true, result.Attempts[^1].Milliseconds, "Соединение восстановлено"));
                DetailText.Text = "Соединение восстановлено; выбран рабочий сервер.";
            }
            else if (!closed) DetailText.Text = "Рабочий сервер не найден. Автоподбор завершён; подключи вручную.";
        }
        catch (OperationCanceledException) { }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        finally { findingRoute = false; operation.Dispose(); operation = null; engineOperation = false; if (manualStop) engine.Stop(); if (!closed) UpdateControls(); }
    }
}

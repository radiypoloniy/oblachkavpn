using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Windows;

namespace Oblachka;

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
    // Переключать сервер при обрыве: по настройке или потому что выбран «Автовыбор» —
    // выбрав его, человек сам попросил, чтобы сервер подбирался без него.
    // Бесплатный чужой узел не восстанавливается сам ни повтором, ни сменой сервера (решение владельца
    // 24.10.2026): упал — человек сам выбирает, к чему подключаться дальше.
    private bool AutoSwitching => (autoSelect || AutoRecover.IsChecked == true) && (runningProfile ?? profile)?.IsFree != true;

    // Проверка связи идёт всегда: без неё чип под тумблером не узнает, что туннель работает.
    // Автовосстановление — отдельное решение: по настройке или при «Автовыборе».
    private async void PollHealth()
    {
        if (closed || vmTestActive || !backgroundWorkEnabled || operation is not null || recoveryWait is not null || engine.ProcessId is null ||
            DateTimeOffset.UtcNow < nextHealthCheck) return;
        nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(45);
        operation = new CancellationTokenSource(); engineOperation = true; backgroundCheck = true; UpdateControls();
        var shouldRecover = false;
        string? pathFailure = null;
        try
        {
            var round = await health.CheckAsync(engine.ProbeAsync, TunnelHealth.Targets, operation.Token);
            if (!closed && !manualStop)
            {
                RecordProbe(round.Results.FirstOrDefault(r => r.Success) ?? round.Results[^1]);
                // При «Автовыборе» обрыв подтверждается быстрее: вторая проверка через 10 с, и двух
                // неудачных подряд достаточно. Иначе — три проверки, повтор через 20 с.
                var limit = autoSelect ? 2 : 3;
                shouldRecover = health.FailedRounds >= limit;
                if (!round.Reachable)
                {
                    nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(autoSelect ? 10 : 20);
                    DetailText.Text = $"Проверка связи не прошла: {health.FailedRounds} из {limit}. Проверяем два независимых сайта.";
                }
                // Ядро отвечает — проверяем путь, которым ходят программы (SystemPathProbe).
                else if (await CheckSystemPathAsync(operation.Token) is { } problem && !closed && !manualStop)
                {
                    RecordProbe(new(false, 0, problem, ProbeFailure.Reachability));
                    if (SystemPathShouldRepair) pathFailure = problem;
                    else { nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(10); DetailText.Text = problem + " Проверяем ещё раз."; }
                }
                else if (!closed && !manualStop) SystemPathHealthy();
            }
        }
        catch (OperationCanceledException) { }
        catch (UserError) { /* Engine exit has its own bounded recovery owner. */ }
        finally
        {
            operation.Dispose(); operation = null; engineOperation = false; backgroundCheck = false;
            if (manualStop) engine.Stop();
            if (!closed) UpdateControls();
        }
        // Путь программ чинится и без автовосстановления: иначе интернет закрыт молча.
        if (!closed && !vmTestActive && !manualStop && pathFailure is not null) { await RepairSystemPathAsync(pathFailure); return; }
        if (!closed && !manualStop && shouldRecover && (runningProfile ?? profile)?.IsFree == true)
            DetailText.Text = "Бесплатный сервер не отвечает. Выберите другой и подключитесь заново — сам он не переключается.";
        if (closed || vmTestActive || manualStop || !AutoSwitching) return;
        if (shouldRecover) await RecoverUnreachableAsync();
        else if (engine.State == ConnectionState.Failed) ScheduleRecovery();
    }
    private async Task RecoverUnreachableAsync()
    {
        if (operation is not null || profile is null) return;
        var delay = recovery.NextDelay(DateTimeOffset.UtcNow);
        if (delay is null) { networkClosed = SystemTunnelActive; DetailText.Text = "Автовосстановление исчерпало три попытки за две минуты. Подключитесь вручную."; nextHealthCheck = DateTimeOffset.MaxValue; RefreshHome(); return; }
        operation = new CancellationTokenSource(); engineOperation = true; findingRoute = true; UpdateControls();
        try
        {
            await Task.Delay(delay.Value, operation.Token);
            var profiles = importedProfiles.ToArray();
            int[] candidates; int start;
            if (autoSelect)
            {
                // Упавший узел помечается: ни автовыбор, ни следующий подбор не начнут с него.
                MarkMeasured(profile, null);
                candidates = RouteCandidates.ByLatency(RouteCandidates.For(profiles, profile, anyNode: true, InAutoPool), profiles, LatestMeasurement, failed: profile);
                start = 0;
            }
            else { candidates = RouteCandidates.For(profiles, profile, pool: InAutoPool); start = RouteCandidates.StartAt(candidates, profiles, profile); }
            // Сначала — почему упало (адрес, рукопожатие, туннель), затем попытки в этом порядке.
            var subset = candidates.Select(i => profiles[i]).ToArray();
            var order = new[] { start }.Concat(Enumerable.Range(0, subset.Length).Where(i => i != start)).ToArray();
            var steps = await PlanRecoveryAsync(subset, order, profile, operation.Token);
            var result = await CreateRouteSearch().FindAsync(subset, steps, TunnelHealth.Targets[0], operation.Token);
            // Итог каждой попытки — тоже замер: автовыбор учтёт его в следующий раз.
            foreach (var attempt in result.Attempts)
                MarkMeasured(profiles[candidates[attempt.ProfileIndex]], attempt.Outcome == "Verified" ? attempt.Milliseconds : null);
            // Нумерация попыток возвращается к позициям в общем списке: отчёт диагностики
            // должен ссылаться на то, что видит пользователь.
            routeAttempts = result.Attempts.Select(a => a with { ProfileIndex = candidates[a.ProfileIndex] }).ToArray();
            if (result.ProfileIndex is int found && !closed)
            {
                var index = candidates[found];
                Servers.SearchBox.Clear(); ProfilePicker.ItemsSource = importedProfiles;
                ProfilePicker.SelectedIndex = index; profile = profiles[index]; runningProfile = profile; health.Reset();
                RecordProbe(new(true, result.Attempts[^1].Milliseconds, "Соединение восстановлено"));
                LearnFragment(result.Fragment);
                DetailText.Text = result.Fragment ? "Соединение восстановлено с обходом DPI: рукопожатие разрезано." : "Соединение восстановлено; выбран рабочий сервер.";
            }
            else if (!closed)
            {
                // Под защитой сеть остаётся закрытой: главный экран покажет «Интернет закрыт».
                networkClosed = SystemTunnelActive;
                DetailText.Text = SystemTunnelActive
                    ? "Рабочий сервер не найден. Блокировка остаётся: выберите сервер и подключитесь или нажмите «Остановить»."
                    : "Рабочий сервер не найден. Автоподбор завершён; подключитесь вручную.";
            }
        }
        catch (OperationCanceledException) { }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        finally { findingRoute = false; operation.Dispose(); operation = null; engineOperation = false; if (manualStop) engine.Stop(); if (!closed) UpdateControls(); }
    }
}

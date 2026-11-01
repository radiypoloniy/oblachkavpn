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
        if (!enabled) return;
        backgroundWorkEnabled = true;
        runtime.Monitor.StateChanged += HealthStateChanged;
        runtime.Monitor.Completed += HealthCompleted;
        runtime.Monitor.Start(new DispatcherSynchronizationContext(Dispatcher));
    }
    private void UnsubscribeLifecycle()
    {
        runtime.Monitor.StateChanged -= HealthStateChanged;
        runtime.Monitor.Completed -= HealthCompleted;
        backgroundWorkEnabled = false;
    }
    private void HealthStateChanged() { if (!closed) UpdateControls(); }
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
            if (runtime.Monitor.Path.ShouldRepair) { await RepairSystemPathAsync(problem); return; }
            DetailText.Text = problem + " Проверяем ещё раз.";
        }
        else SystemPathHealthy();
        if (observation.ShouldRecover && (runningProfile ?? profile)?.IsFree == true)
            DetailText.Text = "Бесплатный сервер не отвечает. Выберите другой и подключитесь заново — сам он не переключается.";
        if (!AutoSwitching) return;
        if (observation.ShouldRecover) await RecoverUnreachableAsync();
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
            else { candidates = RouteCandidates.For(profiles, profile, pool: InAutoPool, machine: machineMode); start = RouteCandidates.StartAt(candidates, profiles, profile); }
            var subset = candidates.Select(i => profiles[i]).ToArray();
            var order = new[] { start }.Concat(Enumerable.Range(0, subset.Length).Where(i => i != start)).ToArray();
            // Выбрана машина — сначала её другие протоколы, без разбора причины: та же страна и тот же
            // выходной адрес, а разбор (до 12 с) нужен, только чтобы выбирать среди других серверов.
            // Протокол, которого нет в наборе защиты сессии (подключались к одному протоколу, машину выбрали
            // потом), на ходу не взять: служба отвергнет. На него — переподключение после подбора (ниже).
            var machine = ServerMachines.Key(profile);
            bool InSession(Profile p) => !SystemTunnelActive || systemTunnel is not { } s || s.CanSwitchTo(p);
            var same = machineMode ? order.Where(i => i != start && ServerMachines.Key(subset[i]) == machine).ToArray() : [];
            var siblings = same.Where(i => InSession(subset[i])).Select(i => new LadderStep(i, false)).ToArray();
            var outside = same.Where(i => !InSession(subset[i])).ToArray();
            var search = CreateRouteSearch();
            var result = siblings.Length > 0 ? await search.FindAsync(subset, siblings, TunnelHealth.Targets[0], operation.Token) : null;
            if (result?.ProfileIndex is null && outside.Length > 0) reconnectTo = subset[outside[0]];
            else if (result?.ProfileIndex is null)
            {
                // Затем — почему упало (адрес, рукопожатие, туннель) и попытки в этом порядке; уже проверенные — не заново.
                var steps = (await PlanRecoveryAsync(subset, order, profile, operation.Token)).Where(s => !siblings.Contains(s)).ToArray();
                var rest = await search.FindAsync(subset, steps, TunnelHealth.Targets[0], operation.Token);
                result = rest with { Attempts = [.. result?.Attempts ?? [], .. rest.Attempts] };
            }
            if (reconnectTo is null) ApplyRecovery(profiles, candidates, result ?? new(null, []));
        }
        catch (OperationCanceledException) { reconnectTo = null; }
        catch (UserError ex) { reconnectTo = null; if (!closed) DetailText.Text = ex.Message; }
        finally { findingRoute = false; operation.Dispose(); operation = null; engineOperation = false; if (manualStop) engine.Stop(); if (!closed) UpdateControls(); }
        if (reconnectTo is { } next && !closed && !manualStop)
        {
            reconnectTo = null;
            // Та же страна, но протокола нет в наборе защиты: сессия ставится заново, на секунды защита снимается.
            Notify(Notice.ServerSwitched, "Сервер сменился", profile.DisplayName + " перестал отвечать — переподключаемся на " + next.DisplayName + ".");
            DetailText.Text = profile.DisplayName + " не отвечает. Переподключаемся на " + next.DisplayName + ": на это время защита снимается.";
            SelectProfile(next); profile = next;
            await StopSystemTunnelAsync();
            UpdateControls();
            if (!closed && ConnectButton.IsEnabled) await ConnectCoreAsync();
        }
    }

    // Подбор нашёл, что дальше — только переподключение (протокол той же машины вне набора защиты).
    private Profile? reconnectTo;

    private void ApplyRecovery(Profile[] profiles, int[] candidates, RouteSearchResult result)
    {
        if (profile is null) return;
        // Итог каждой попытки — тоже замер: автовыбор учтёт его в следующий раз.
        foreach (var attempt in result.Attempts)
            MarkMeasured(profiles[candidates[attempt.ProfileIndex]], attempt.Outcome == "Verified" ? attempt.Milliseconds : null);
        // Нумерация попыток возвращается к позициям в общем списке: отчёт диагностики
        // должен ссылаться на то, что видит пользователь.
        routeAttempts = result.Attempts.Select(a => a with { ProfileIndex = candidates[a.ProfileIndex] }).ToArray();
        if (result.ProfileIndex is int found && !closed)
        {
            var index = candidates[found];
            if (!ReferenceEquals(profiles[index], profile))
                Notify(Notice.ServerSwitched, "Сервер сменился", profile.DisplayName + " перестал отвечать — теперь " + profiles[index].DisplayName + ".");
            Servers.SearchBox.Clear(); ProfilePicker.ItemsSource = importedProfiles;
            ProfilePicker.SelectedIndex = index; profile = profiles[index]; runningProfile = profile; health.Reset(); verifiedSinceStart = true;
            RecordProbe(new(true, result.Attempts[^1].Milliseconds, "Соединение восстановлено"));
            LearnFragment(result.Fragment);
            DetailText.Text = result.Fragment ? "Соединение восстановлено с обходом DPI: рукопожатие разрезано." : "Соединение восстановлено; выбран рабочий сервер.";
        }
        else if (!closed)
        {
            // Под защитой сеть остаётся закрытой: главный экран покажет «Интернет закрыт».
            networkClosed = SystemTunnelActive;
            Notify(Notice.NetworkClosed, SystemTunnelActive ? "Интернет закрыт" : "VPN не работает", "Рабочий сервер не найден. Нажмите, чтобы выбрать другой или выключить VPN.", warning: true);
            DetailText.Text = SystemTunnelActive
                ? "Рабочий сервер не найден. Блокировка остаётся: выберите сервер и подключитесь или нажмите «Остановить»."
                : "Рабочий сервер не найден. Автоподбор завершён; подключитесь вручную.";
        }
    }

}

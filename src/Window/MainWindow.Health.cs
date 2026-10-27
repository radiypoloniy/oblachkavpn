using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private readonly TunnelHealth health = new();
    private DateTimeOffset nextHealthCheck = DateTimeOffset.MaxValue;
    // Через этот протокол с подключения ещё не прошёл ни один запрос. Пока так, ждать трёх неудач
    // незачем: терять нечего, а у машины есть соседний протокол на том же адресе (0.21, ускорение).
    private bool verifiedSinceStart;
    private static readonly TimeSpan FreshBudget = TimeSpan.FromSeconds(5);
    // Первая проверка после подключения или смены сервера: ядру хватает секунды, чтобы взять маршрут.
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(1);

    private void StartedFresh() { verifiedSinceStart = false; nextHealthCheck = DateTimeOffset.UtcNow + FirstCheck; }
    private bool HasSibling(Profile item) => machineMode && MachineOf(item).Count > 1;
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
        // Идёт трафик — проверяем раз в 20 с: обрыв заметен раньше; в простое — раз в 45 с.
        nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(DateTimeOffset.UtcNow - lastTraffic < TimeSpan.FromSeconds(60) ? 20 : 45);
        operation = new CancellationTokenSource(); engineOperation = true; backgroundCheck = true; UpdateControls();
        var shouldRecover = false;
        string? pathFailure = null;
        try
        {
            var fresh = !verifiedSinceStart;
            var round = await health.CheckAsync(engine.ProbeAsync, TunnelHealth.Targets, operation.Token, fresh ? FreshBudget : null);
            if (!closed && !manualStop)
            {
                RecordProbe(round.Results.FirstOrDefault(r => r.Success) ?? round.Results[^1]);
                if (round.Verified) verifiedSinceStart = true;
                // При «Автовыборе» двух неудачных проверок подряд достаточно, иначе — трёх. Повтор через 8 с:
                // каждая проверка и так спрашивает два независимых сайта, долгие паузы только тянули обрыв.
                // Протокол с подключения не ответил ни разу: у машины — сразу соседний, иначе — после второй.
                var limit = fresh ? (profile is { } p && HasSibling(p) ? 1 : 2) : autoSelect ? 2 : 3;
                shouldRecover = health.FailedRounds >= limit;
                if (!round.Reachable)
                {
                    nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(fresh ? 3 : 8);
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

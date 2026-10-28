namespace Oblachka;

public partial class MainWindow
{
    private readonly Dictionary<string, ServerMeasurement> measurements = new();
    private void WireMeasurements()
    {
        // «Обновить»: сначала подписки (значок со стрелками ждут именно этого — новый сервер в панели
        // иначе приходил только по расписанию), затем задержка через каждый сервер, и при подключении тоже.
        // Идёт подключение — подписка обновится по расписанию, пинг меряется сразу.
        Servers.MeasureButton.Click += async (_, _) =>
        {
            if (measuring) { measureCancel?.Cancel(); return; }
            if (operation is null) await RefreshAllAsync();
            if (!closed) await MeasureServersAsync(importedProfiles.ToArray(), quiet: false);
        };
        // «Подобрать лучшее» — MainWindow.Tune; «Автовыбор» подключает по готовым замерам.
        WireTune();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => addressChanged = DateTimeOffset.UtcNow.Ticks;
    }

    // Когда сменились адреса (другой Wi-Fi, кабель, наш туннель): тики UTC, 0 — не было.
    private long addressChanged;
    // «Проверить этот сервер»: настоящий HTTPS-запрос через выбранный сервер отдельным ядром,
    // как у «Проверить все», но для одного и любого источника. Подключение не трогает.
    private Profile? checkingServer;
    private async Task CheckServerAsync(Profile item)
    {
        if (checkingServer is not null || measuring) { DetailText.Text = "Дождитесь окончания текущей проверки."; return; }
        // Под «Весь компьютер» — через адрес, который защита разрешила ядрам.
        if (MeasureRoute(item) is null) { DetailText.Text = item.DisplayName + ": при «Весь компьютер» его адрес закрыт защитой. Переподключитесь, чтобы проверить."; return; }
        checkingServer = item; RenderSheet();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            ServerMeasurement? result = null;
            await ServerMeasurements.MeasureAsync(new[] { item }, RouteEngine, measured => result = measured, timeout.Token, MeasureRoute);
            if (result is not null) { measurements[result.Fingerprint] = result; QueueStateSave(); }
            if (!closed) DetailText.Text = result is { Success: true } ? $"{item.DisplayName}: работает, {result.Milliseconds} мс." : $"{item.DisplayName}: не ответил.";
        }
        catch (OperationCanceledException) { if (!closed) DetailText.Text = item.DisplayName + ": проверка не уложилась в 45 секунд."; }
        finally { checkingServer = null; if (!closed) { RenderSheet(); RefreshHome(); } }
    }

    // Отклики серверов (ServerPing) живут только в этом запуске: они дешёвые и делаются заново
    // при каждом старте, после загрузки подписки и при открытии «Всех серверов».
    private readonly Dictionary<string, ServerMeasurement> pings = new();
    private CancellationTokenSource? pinging;
    private bool pingQueued;
    private DateTimeOffset lastPing = DateTimeOffset.MinValue;

    // Под «Весь компьютер» соединения окна либо закрыты защитой, либо уходят в туннель
    // и меряли бы его, а не сервер. Тогда отклики не обновляем, видны прежние.
    // Окна проверок и превью (без сохранения состояния) в сеть не ходят.
    private bool CanPing => systemTunnel is null && !closed && importedProfiles.Count > 0 && stateStore is not null && !isolatedPersistence;

    private Task pingTask = Task.CompletedTask;

    private void QueuePing()
    {
        if (pinging is not null) { pingQueued = true; return; }
        if (!CanPing) return;
        pingTask = PingServersAsync();
    }

    private async Task WaitForPingsAsync()
    {
        if (pinging is null && pings.Count > 0) return;
        if (pinging is null) QueuePing();
        await Task.WhenAny(pingTask, Task.Delay(TimeSpan.FromSeconds(3)));
    }

    private async Task PingServersAsync()
    {
        pinging = new CancellationTokenSource(TimeSpan.FromSeconds(20)); pingQueued = false;
        var token = pinging.Token;
        var targets = importedProfiles.ToArray();
        try
        {
            await ServerPing.PingAllAsync(targets, (item, milliseconds) => Dispatcher.BeginInvoke(() =>
            {
                // Подключение в режиме «Весь компьютер» началось во время замера: ответы, пришедшие
                // после этого, мерили бы туннель или упирались бы в защиту.
                if (closed || token.IsCancellationRequested || !CanPing) return;
                var key = Profiles.Fingerprint(item);
                pings[key] = new(key, DateTimeOffset.UtcNow, milliseconds is not null, milliseconds ?? 0) { Handshake = true };
                PingsChanged();
            }), token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lastPing = DateTimeOffset.UtcNow;
            pinging.Dispose(); pinging = null;
            if (pingQueued && !closed) QueuePing();
        }
    }

    // Итог проверки узла в работе (подбор, автовосстановление): null — не ответил.
    private void MarkMeasured(Profile item, long? milliseconds)
    {
        var key = Profiles.Fingerprint(item);
        pings[key] = new(key, DateTimeOffset.UtcNow, milliseconds is not null, Math.Max(1, milliseconds ?? 0));
        PingsChanged();
    }

    // Пока окно открыто, отклики обновляются раз в 10 минут; после выключения режима
    // «Весь компьютер» это же обновляет устаревшие. Задержка через серверы пула автовыбора —
    // тоже раз в 10 минут и при любом режиме: по ней работает «Автовыбор».
    private void PollPing()
    {
        var age = DateTimeOffset.UtcNow - lastPing;
        // Откликов нет совсем (замер оборвало подключение «Весь компьютер») — повтор вскоре после выключения.
        if (IsVisible && (age > TimeSpan.FromMinutes(10) || pings.Count == 0 && age > TimeSpan.FromSeconds(30))) QueuePing();
        // Сеть сменилась — замер через 15 секунд, когда она устоится, а не через час.
        // Свой туннель адреса тоже меняет, но NetworkIdentity его не считает: сеть та же.
        if (Interlocked.Read(ref addressChanged) is var changed and > 0 && DateTimeOffset.UtcNow.Ticks - changed > TimeSpan.TicksPerSecond * 15 && !measuring)
        {
            Interlocked.CompareExchange(ref addressChanged, 0, changed);
            if (throughputNetwork is not null && NetworkIdentity.Current() is { } now && now != throughputNetwork) lastMeasure = DateTimeOffset.MinValue;
        }
        if (!measuring && tuning is null && operation is null && importedProfiles.Count > 0 && stateStore is not null && !isolatedPersistence &&
            DateTimeOffset.UtcNow - lastMeasure > TimeSpan.FromMinutes(10))
            _ = MeasureServersAsync(autoPool.Members(importedProfiles.ToArray()), quiet: true);
    }

    // Отклики приходят по одному: перестраиваем видимое не чаще раза в 150 мс.
    private bool pingRefreshQueued;
    private void PingsChanged()
    {
        if (pingRefreshQueued) return;
        pingRefreshQueued = true;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(150);
            pingRefreshQueued = false;
            if (closed) return;
            if (Servers.Visibility == System.Windows.Visibility.Visible) FilterProfiles();
            RefreshHome();
        });
    }

    // Что показать у сервера: свежий полный замер точнее отклика, отклик свежее старого замера.
    private ServerMeasurement? LatestMeasurement(Profile item)
    {
        var key = Profiles.Fingerprint(item);
        var now = DateTimeOffset.UtcNow;
        measurements.TryGetValue(key, out var full);
        if (full is not null && ServerMeasurements.Fresh(full, now)) return full;
        return pings.TryGetValue(key, out var ping) ? ping : full;
    }

    private Dictionary<string, ServerMeasurement> EffectiveMeasurements() =>
        importedProfiles.Select(LatestMeasurement).OfType<ServerMeasurement>()
            .GroupBy(m => m.Fingerprint).ToDictionary(g => g.Key, g => g.First());

    // «Пинг обновлён 3 мин назад» в подвале «Серверов»: давность последнего замера.
    private void RefreshMeasurement()
    {
        var last = measurements.Values.Concat(pings.Values).Select(m => (DateTimeOffset?)m.Time).Max();
        var age = last is null ? (TimeSpan?)null : DateTimeOffset.UtcNow - last.Value;
        Servers.MeasuredAgo.Text = age is null ? "" : "Пинг обновлён " + (age < TimeSpan.FromMinutes(1) ? "только что" : age < TimeSpan.FromHours(1) ? (int)age.Value.TotalMinutes + " мин назад" : "в " + last!.Value.ToLocalTime().ToString("HH:mm"));
    }
}

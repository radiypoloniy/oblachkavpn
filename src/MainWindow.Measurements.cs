namespace Oblachka;

public partial class MainWindow
{
    private readonly Dictionary<string, ServerMeasurement> measurements = new();
    private void WireMeasurements()
    {
        Servers.MeasureButton.Click += async (_, _) =>
        {
            if (operation is not null || profile is null) return;
            if (!importedProfiles.Any(p => p.IsService)) { DetailText.Text = "Измерения выполняются только по узлам вашей подписки."; return; }
            operation = new CancellationTokenSource(); measuring = true; UpdateControls();
            try
            {
                // Измеряем только узлы сервиса: сторонние конфигурации нам не принадлежат,
                // и отвечать за их задержку и доступность мы не можем.
                var measured = importedProfiles.Where(p => p.IsService).ToArray();
                var done = 0;
                await ServerMeasurements.MeasureAsync(measured,
                    () => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), ""),
                    result => { measurements[result.Fingerprint] = result; if (!closed) { DetailText.Text = $"Проверено серверов: {++done} из {measured.Length}. Можно остановить."; RenderSheet(); RefreshHome(); } }, operation.Token);
                QueueStateSave();
            }
            catch (OperationCanceledException) { if (!closed) DetailText.Text = "Измерение завершено по отмене или лимиту пяти минут. Полученные результаты сохранены."; QueueStateSave(); }
            finally { operation.Dispose(); operation = null; measuring = false; if (!closed) UpdateControls(); }
        };
        // «Подключить лучший» — то же, что «Автовыбор»: лучший по свежим замерам и сразу подключение.
        Servers.BestButton.Click += (_, _) => { if (operation is null) { PickServer(null); Navigate("Connection"); } };
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

    private void QueuePing()
    {
        if (pinging is not null) { pingQueued = true; return; }
        if (!CanPing) return;
        _ = PingServersAsync();
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
                if (closed || token.IsCancellationRequested) return;
                var key = Profiles.Fingerprint(item);
                pings[key] = new(key, DateTimeOffset.UtcNow, milliseconds is not null, milliseconds ?? 0);
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

    // Пока окно открыто, отклики обновляются раз в 10 минут; после выключения режима
    // «Весь компьютер» это же обновляет устаревшие.
    private void PollPing()
    {
        if (IsVisible && DateTimeOffset.UtcNow - lastPing > TimeSpan.FromMinutes(10)) QueuePing();
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

    // «3 мин» над списком: давность последнего замера.
    private void RefreshMeasurement()
    {
        var last = measurements.Values.Concat(pings.Values).Select(m => (DateTimeOffset?)m.Time).Max();
        var age = last is null ? (TimeSpan?)null : DateTimeOffset.UtcNow - last.Value;
        Servers.MeasuredAgo.Text = age is null ? "" : age < TimeSpan.FromMinutes(1) ? "только что" : age < TimeSpan.FromHours(1) ? (int)age.Value.TotalMinutes + " мин" : last!.Value.ToLocalTime().ToString("HH:mm");
    }
}

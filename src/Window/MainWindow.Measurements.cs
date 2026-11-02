namespace Oblachka;

public partial class MainWindow
{
    private Dictionary<string, ServerMeasurement> measurements => runtime.Profiles.Measurements;
    private void WireMeasurements()
    {
        // «Обновить»: сначала подписки (значок со стрелками ждут именно этого — новый сервер в панели
        // иначе приходил только по расписанию), затем задержка через каждый сервер, и при подключении тоже.
        // Идёт подключение — подписка обновится по расписанию, пинг меряется сразу.
        Servers.MeasureButton.Click += async (_, _) =>
        {
            if (measuring) { runtime.Measurements.CancelMeasure(); return; }
            if (operation is null) await RefreshAllAsync();
            if (!closed) await MeasureServersAsync(importedProfiles.ToArray(), quiet: false);
        };
        // «Подобрать лучшее» — MainWindow.Tune; «Автовыбор» подключает по готовым замерам.
        WireTune();
    }

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
        finally { checkingServer = null; TrimMeasurements(); if (!closed) { RenderSheet(); RefreshHome(); } }
    }

    // Отклики серверов (ServerPing) живут только в этом запуске: они дешёвые и делаются заново
    // при каждом старте, после загрузки подписки и при открытии «Всех серверов».
    private Dictionary<string, ServerMeasurement> pings => runtime.Profiles.Pings;
    private DateTimeOffset lastPing => runtime.Measurements.LastPing;
    private void QueuePing() => runtime.Measurements.QueuePing();
    private Task WaitForPingsAsync() => runtime.Measurements.WaitForPingsAsync();

    // Итог проверки узла в работе (подбор, автовосстановление): null — не ответил.
    private void MarkMeasured(Profile item, long? milliseconds)
    {
        runtime.Profiles.MarkMeasured(item, milliseconds);
        PingsChanged();
    }

    // Отклики приходят по одному: перестраиваем видимое не чаще раза в 150 мс.
    private bool pingRefreshQueued;
    private bool sheetMeasurementsDirty;
    private void PingsChanged()
    {
        sheetMeasurementsDirty = true;
        if (pingRefreshQueued) return;
        pingRefreshQueued = true;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(150);
            pingRefreshQueued = false;
            if (closed) return;
            if (!IsVisible || WindowState == System.Windows.WindowState.Minimized) return;
            if (Servers.IsVisible) FilterProfiles();
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

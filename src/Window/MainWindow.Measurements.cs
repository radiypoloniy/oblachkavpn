namespace Oblachka;

public partial class MainWindow
{
    private Dictionary<string, ServerMeasurement> measurements => runtime.Profiles.Measurements;
    private void WireMeasurements()
    {
        // Проверка задержки отделена от загрузки списка и ограничена открытой вкладкой.
        Servers.MeasureButton.Click += async (_, _) =>
        {
            if (measuring) { runtime.Measurements.CancelMeasure(); return; }
            if (operation is null && !closed) await MeasureServersAsync(SubscriptionScopeProfiles().ToArray(), quiet: false);
        };
        // «Подобрать лучшее» — MainWindow.Tune; «Автовыбор» подключает по готовым замерам.
        WireTune();
    }

    private Profile? checkingServer;
    private async Task CheckServersAsync(IReadOnlyList<Profile> targets, bool speed = false)
    {
        if (measuring || tuning is not null || operation is not null)
        { DetailText.Text = "Дождитесь окончания текущей операции."; return; }
        checkingServer = targets.Count == 1 ? targets[0] : null;
        try { await runtime.Measurements.MeasureAsync(targets, quiet: false, speed: speed); }
        finally { checkingServer = null; if (!closed) PingsChanged(); }
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
        Servers.MeasuredAgo.Text = !runtime.Measurements.CanMeasure ? "Проверка серверов — при выключенном VPN"
            : age is null || age >= TimeSpan.FromMinutes(20) ? "" : "Проверка " + (age < TimeSpan.FromMinutes(1) ? "только что" : (int)age.Value.TotalMinutes + " мин назад");
    }
}

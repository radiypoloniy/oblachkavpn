namespace RoutePilot;

public partial class MainWindow
{
    private readonly Dictionary<string, ServerMeasurement> measurements = new();
    private void WireMeasurements()
    {
        Servers.MeasureButton.Click += async (_, _) =>
        {
            if (operation is not null || engine.ProcessId is not null || profile is null) return;
            operation = new CancellationTokenSource(); UpdateControls();
            try
            {
                var done = 0;
                await ServerMeasurements.MeasureAsync(importedProfiles.ToArray(),
                    () => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), ""),
                    result => { measurements[result.Fingerprint] = result; if (!closed) { DetailText.Text = $"Проверено серверов: {++done} из {importedProfiles.Count}. Можно остановить."; RefreshMeasurement(); } }, operation.Token);
                QueueStateSave();
            }
            catch (OperationCanceledException) { if (!closed) DetailText.Text = "Измерение завершено по отмене или лимиту пяти минут. Полученные результаты сохранены."; QueueStateSave(); }
            finally { operation.Dispose(); operation = null; if (!closed) UpdateControls(); }
        };
        Servers.BestButton.Click += (_, _) =>
        {
            if (operation is not null || engine.ProcessId is not null) return;
            var best = ServerMeasurements.Best(importedProfiles, measurements, DateTimeOffset.UtcNow);
            if (best is null) { DetailText.Text = "Нет успешных измерений за последние 20 минут. Сначала проверьте серверы."; return; }
            Servers.SearchBox.Clear(); ProfilePicker.ItemsSource = importedProfiles; ProfilePicker.SelectedIndex = best.Value;
            DetailText.Text = "Выбран сервер с наименьшим временем успешного HTTPS-запроса.";
        };
    }
    private void RefreshMeasurement()
    {
        if (profile is null || !measurements.TryGetValue(Profiles.Fingerprint(profile), out var result)) { Servers.MeasurementDetails.Text = "Ещё не проверяли. Измерение выполняет HTTPS-запрос через сервер."; return; }
        var age = DateTimeOffset.UtcNow - result.Time;
        Servers.MeasurementDetails.Text = (result.Success ? $"Запрос: {result.Milliseconds} мс" : "Проверка не прошла") + $" · {result.Time.ToLocalTime():HH:mm}" + (age >= TimeSpan.FromMinutes(20) ? " · устарело" : "") + ". Это время запроса, не ping и не скорость.";
    }
}

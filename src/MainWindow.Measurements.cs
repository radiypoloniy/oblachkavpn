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
    // «3 мин» над списком: давность последнего замера.
    private void RefreshMeasurement()
    {
        var last = measurements.Values.Select(m => (DateTimeOffset?)m.Time).Max();
        var age = last is null ? (TimeSpan?)null : DateTimeOffset.UtcNow - last.Value;
        Servers.MeasuredAgo.Text = age is null ? "" : age < TimeSpan.FromMinutes(1) ? "только что" : age < TimeSpan.FromHours(1) ? (int)age.Value.TotalMinutes + " мин" : last!.Value.ToLocalTime().ToString("HH:mm");
    }
}

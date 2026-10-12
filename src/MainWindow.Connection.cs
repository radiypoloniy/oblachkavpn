using System.Windows;

namespace Oblachka;

// Жизненный цикл подключения: запуск, остановка и выбор между локальным режимом
// и системным. Выделено из MainWindow.xaml.cs, когда файл перерос лимит размера.
public partial class MainWindow
{
    private async void ConnectClick(object sender, RoutedEventArgs args)
    {
        manualStop = false;
        recoveryWait?.Cancel(); recovery.Reset(); health.Reset();
        await ConnectCoreAsync();
    }
    private async Task ConnectCoreAsync()
    {
        if (vmTestActive || profile is null || operation is not null) return;
        manualStop = false;
        operation = new CancellationTokenSource();
        engineOperation = true;
        UpdateControls();
        try
        {
            // В системном режиме порядок запуска держит SystemTunnelSession: сначала ядро,
            // затем туннель у привилегированного помощника. Окно само ядро не стартует.
            if (SystemTunnelWanted)
            {
                await StartSystemTunnelAsync(operation.Token);
                nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(3);
                if (!closed) DetailText.Text = "Системный режим запущен: весь трафик идёт через туннель.";
            }
            else
            {
                await engine.StartAsync(profile, operation.Token);
                nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(3);
                if (!closed) DetailText.Text = "Подключение запущено. Проверьте туннель в диагностике.";
            }
        }
        catch (OperationCanceledException) { if (!closed) DetailText.Text = "Запуск отменён или истёк срок ожидания. Можно попробовать снова."; }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        finally { if (manualStop) engine.Stop(); operation.Dispose(); operation = null; engineOperation = false; if (!closed) { UpdateControls(); if (engine.State == ConnectionState.Failed) ScheduleRecovery(); } }
    }
    private void StopClick(object sender, RoutedEventArgs args)
    {
        manualStop = true;
        recoveryWait?.Cancel();
        operation?.Cancel();
        // Системный режим снимает туннель и только потом ядро: иначе маршруты остались бы
        // указывать на мёртвый локальный порт.
        if (systemTunnel is not null) { _ = StopSystemTunnelAsync(); DetailText.Text = "Останавливаем системный режим…"; return; }
        // During startup the async owner tears down the engine; do not race process disposal with it.
        if (operation is null || !engineOperation) engine.Stop();
        else if (engine.State is ConnectionState.Running or ConnectionState.Verified) DetailText.Text = "Отменяем проверку…";
        if (operation is null) DetailText.Text = "Подключение остановлено.";
    }
}

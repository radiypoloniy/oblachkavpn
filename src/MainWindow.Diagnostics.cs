using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace RoutePilot;

public partial class MainWindow
{
    private async void ScheduleRecovery()
    {
        if (closed || vmTestActive || manualStop || operation is not null || recoveryWait is not null || AutoRecover.IsChecked != true || profile is null) return;
        var delay = recovery.NextDelay(DateTimeOffset.UtcNow);
        if (delay is null) { DetailText.Text = "Автоперезапуск остановлен после трёх попыток за две минуты. Проверьте профиль и подключи вручную."; return; }
        recoveryWait = new CancellationTokenSource();
        try
        {
            DetailText.Text = $"Ядро остановилось. Повторный запуск через {delay.Value.TotalSeconds:0} с…";
            await Task.Delay(delay.Value, recoveryWait.Token);
            if (!closed && !manualStop && operation is null && engine.State == ConnectionState.Failed && AutoRecover.IsChecked == true)
            { engine.Stop(); await ConnectCoreAsync(); }
        }
        catch (OperationCanceledException) { }
        finally
        {
            recoveryWait.Dispose(); recoveryWait = null;
            if (!closed && !manualStop && engine.State == ConnectionState.Failed) ScheduleRecovery();
        }
    }
    private void ExportReportClick(object sender, RoutedEventArgs args)
    {
        var dialog = new SaveFileDialog { Filter = "Диагностика (*.json)|*.json", FileName = "oblachka-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", Title = "Сохранить обезличенный отчёт" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, DiagnosticReport.Create(engine.State, importedProfiles.Count, subscriptionSources.Count, events, routeAttempts));
            DetailText.Text = "Отчёт сохранён. Профили, UUID и адреса подписок в него не включены.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось сохранить отчёт. Выберите доступную папку."; }
    }
}

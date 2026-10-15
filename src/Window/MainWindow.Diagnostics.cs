using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace Oblachka;

public partial class MainWindow
{
    private async void ScheduleRecovery()
    {
        if (closed || vmTestActive || manualStop || operation is not null || recoveryWait is not null || !AutoSwitching || profile is null) return;
        var delay = recovery.NextDelay(DateTimeOffset.UtcNow);
        if (delay is null) { DetailText.Text = "После трёх попыток за две минуты восстановление приостановлено. Проверьте сеть или выберите другой сервер и подключитесь вручную."; return; }
        recoveryWait = new CancellationTokenSource();
        try
        {
            DetailText.Text = $"Ядро остановилось. Повторный запуск через {delay.Value.TotalSeconds:0} с…";
            await Task.Delay(delay.Value, recoveryWait.Token);
            if (!closed && !manualStop && operation is null && engine.State == ConnectionState.Failed && AutoSwitching)
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
            File.WriteAllText(dialog.FileName, CreateSupportReport());
            DetailText.Text = "Отчёт сохранён. Профили, UUID и адреса подписок в него не включены.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось сохранить отчёт. Выберите доступную папку."; }
    }

    private string CreateSupportReport() => DiagnosticReport.Create(engine.State, importedProfiles.Count,
        groups.Sum(g => g.Sources.Length), events, routeAttempts,
        SystemTunnelActive ? DiagnosticMode.SystemTunnel : systemProxyPort != 0
            ? DiagnosticMode.WindowsProxy : DiagnosticMode.LocalProxy, systemTunnel?.BlocksBypass == true);

    private void CopyReportClick(object sender, RoutedEventArgs args)
    {
        try { Clipboard.SetText(CreateSupportReport()); DetailText.Text = "Обезличенный отчёт скопирован. Его можно отправить в поддержку."; }
        catch (System.Runtime.InteropServices.COMException) { DetailText.Text = "Буфер обмена занят. Попробуйте снова или сохраните отчёт в файл."; }
    }
}

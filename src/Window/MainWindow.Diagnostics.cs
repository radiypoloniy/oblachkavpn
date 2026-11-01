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
    private void ScheduleRecovery() => _ = runtime.Recovery.RestartFailedAsync();
    private async void ExportReportClick(object sender, RoutedEventArgs args)
    {
        var dialog = new SaveFileDialog { Filter = "Диагностика (*.json)|*.json", FileName = "oblachka-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", Title = "Сохранить обезличенный отчёт" };
        if (dialog.ShowDialog(this) != true) return;
        var report = await CreateSupportReportAsync();
        try
        {
            File.WriteAllText(dialog.FileName, report);
            DetailText.Text = "Отчёт сохранён. Ключей, адресов серверов и истории сайтов в нём нет — его можно отправить в поддержку.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось сохранить отчёт. Выберите доступную папку."; }
    }

    private async void CopyReportClick(object sender, RoutedEventArgs args)
    {
        var report = await CreateSupportReportAsync();
        try { Clipboard.SetText(report); DetailText.Text = "Отчёт скопирован — вставьте его в чат поддержки и опишите, что случилось. Лучше сразу, пока программа, с которой проблема, ещё запущена."; }
        catch (System.Runtime.InteropServices.COMException) { DetailText.Text = "Буфер обмена занят. Попробуйте снова или сохраните отчёт в файл."; }
    }
}

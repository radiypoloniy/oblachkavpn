using System.Windows;
using Microsoft.Win32;

namespace Oblachka;

// «Резервная копия» в «Общих» (ClientBackup): сохранить настройки в файл и восстановить из него.
// Восстановление — только при выключенном VPN: снимок меняет серверы и правила под работающим
// туннелем.
public partial class MainWindow
{
    private void InitBackup()
    {
        Settings.BackupSave.Click += (_, _) => SaveBackup();
        Settings.BackupRestore.Click += (_, _) => RestoreBackup();
    }

    private void SaveBackup()
    {
        if (!AppDialog.Confirm(this, "Сохранить настройки в файл?",
            "В файле будут ключи подписок и ваши серверы. Храните его как пароль и никому не пересылайте.", "Сохранить")) return;
        var dialog = new SaveFileDialog { Filter = "Резервная копия Облачки (*.json)|*.json", FileName = "Облачка — настройки " + DateTime.Now.ToString("yyyy-MM-dd") + ".json", Title = "Сохранить резервную копию" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, ClientBackup.Write(CaptureState(), DateTimeOffset.Now));
            DetailText.Text = "Настройки сохранены в файл.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось сохранить файл: " + ex.Message; }
    }

    private void RestoreBackup()
    {
        if (ConnectionActive || systemTunnel is not null || operation is not null)
        {
            DetailText.Text = "Сначала выключите VPN, потом восстанавливайте настройки.";
            return;
        }
        var dialog = new OpenFileDialog { Filter = "Резервная копия Облачки (*.json)|*.json|Все файлы|*.*", Title = "Восстановить из резервной копии" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var (snapshot, saved) = ClientBackup.Read(File.ReadAllBytes(dialog.FileName));
            if (!AppDialog.Confirm(this, "Восстановить настройки" + (saved is { } at ? " от " + at.ToLocalTime().ToString("d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) : "") + "?",
                "Подписки, серверы, правила и настройки станут как в копии. Текущие заменятся.", "Восстановить")) return;
            ApplySnapshot(snapshot);
            QueueStateSave(); FlushStateSave();
            DetailText.Text = "Настройки восстановлены из копии.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось прочитать файл: " + ex.Message; }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }
}

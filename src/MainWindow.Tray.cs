using System.Windows;
using Oblachka.Views;

namespace Oblachka;

public partial class MainWindow
{
    private TrayPopup? trayPopup;

    // Крестик прячет окно, подключение продолжает работать. Один раз за запуск об этом
    // говорится: иначе кажется, что программа закрылась, а VPN почему-то остался.
    private bool trayHintShown;

    // Повторный запуск с ярлыка будит этот экземпляр (AppInstance) из другого потока.
    public void ShowFromSignal() => Dispatcher.BeginInvoke(new Action(ShowWindow));

    private void InitStartup(bool enabled)
    {
        if (!enabled) { Settings.StartWithWindows.IsEnabled = false; return; }
        try { Settings.StartWithWindows.IsChecked = AppInstance.StartsWithWindows; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { Settings.StartWithWindows.IsEnabled = false; }
        // Checked/Unchecked, а не Click: переключение с клавиатуры специальных средств и
        // UI Automation не порождает Click, и выбор не сохранялся бы.
        void Apply(bool on)
        {
            try { if (AppInstance.StartsWithWindows != on) AppInstance.StartsWithWindows = on; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { DetailText.Text = "Не удалось изменить автозапуск."; }
        }
        Settings.StartWithWindows.Checked += (_, _) => Apply(true);
        Settings.StartWithWindows.Unchecked += (_, _) => Apply(false);
    }

    // Запуск из автозапуска: окно создаётся как обычно (по нему инициализируется состояние),
    // но сразу прячется — Облачка в трее.
    public void StartHiddenInTray()
    {
        ShowInTaskbar = false; WindowState = WindowState.Minimized;
        Loaded += (_, _) => { Hide(); ShowInTaskbar = true; WindowState = WindowState.Normal; trayHintShown = true; };
    }
    private void HideToTray()
    {
        Hide();
        if (trayHintShown) return;
        trayHintShown = true;
        tray.ShowBalloonTip(4000, ServiceBrand.Name, "Облачка продолжают работать в трее. Выйти — через меню значка: «Завершить».", System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ShowTrayPopup()
    {
        if (closed) return;
        trayPopup?.Close();
        var popup = new TrayPopup(
            () => { if (StopButton.IsEnabled) StopClick(this, new RoutedEventArgs()); else ConnectClick(this, new RoutedEventArgs()); },
            ShowWindow, ExitApplication);
        trayPopup = popup;
        popup.Closed += (_, _) => { if (ReferenceEquals(trayPopup, popup)) trayPopup = null; };
        RefreshTrayPopup();
        var area = SystemParameters.WorkArea;
        popup.Left = Math.Max(area.Left, area.Right - popup.Width - 12);
        popup.Top = Math.Max(area.Top, area.Bottom - popup.Height - 12);
        popup.Show();
    }

    private void RefreshTrayPopup()
    {
        trayPopup?.Refresh(StatusText.Text, profile?.DisplayName ?? "Добавьте подключение", profile?.Transport ?? "Подписка, ключ или файл",
            lastProbe is { Success: true } ? "Туннель проверен" : "Туннель не проверен",
            sessionStarted is null ? "Туннель выключен" : SessionDuration + " в сети",
            StopButton.IsEnabled, StopButton.IsEnabled || ConnectButton.IsEnabled);
    }

    internal TrayPopup CreateTrayPreview()
    {
        var popup = new TrayPopup(() => { }, () => { }, () => { });
        popup.Refresh(StatusText.Text, profile?.DisplayName ?? "Добавьте подключение", profile?.Transport ?? "Подписка, ключ или файл",
            "Туннель не проверен", "Туннель выключен", false, profile is not null);
        return popup;
    }
}

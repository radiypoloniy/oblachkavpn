using System.Windows;
using Oblachka.Views;

namespace Oblachka;

public partial class MainWindow
{
    private TrayPopup? trayPopup;

    // Крестик прячет окно, подключение продолжает работать. Один раз за запуск об этом
    // говорится: иначе кажется, что программа закрылась, а VPN почему-то остался.
    private bool trayHintShown;
    private void HideToTray()
    {
        Hide();
        if (trayHintShown) return;
        trayHintShown = true;
        tray.ShowBalloonTip(4000, ServiceBrand.Name, "Облачка продолжает работать в трее. Выйти — через меню значка: «Завершить».", System.Windows.Forms.ToolTipIcon.Info);
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
            sessionStarted is null ? "Туннель выключен" : Connection.SessionTime.Text + " в сети",
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

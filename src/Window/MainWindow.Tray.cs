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
    public void ShowFromSignal() => Dispatcher.BeginInvoke(new Action(() =>
    {
        ShowWindow();
        if (DeepLink.TakePending() is { } link) QueueDeepLink(link);
    }));

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
    // Подсказка «работаем в трее» — один раз вообще, а не на каждый крестик и не на каждый запуск.
    // Отметка — отдельным файлом: файл состояния до первой подписки не пишется.
    private static string TrayHintMarker => Path.Combine(ClientDataPaths.Root, "tray-hint-shown");

    private void HideToTray()
    {
        Hide();
        if (trayHintShown || File.Exists(TrayHintMarker)) return;
        trayHintShown = true;
        if (stateStore is not null && !isolatedPersistence)
            try { Directory.CreateDirectory(ClientDataPaths.Root); File.WriteAllText(TrayHintMarker, DateTimeOffset.UtcNow.ToString("O")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        tray.ShowBalloonTip(4000, ServiceBrand.Name, "Облачка продолжают работать в трее. Выйти — через меню значка: «Завершить».", System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ShowTrayPopup()
    {
        if (closed) return;
        // Повторный щелчок по значку закрывает окно, а не открывает заново.
        if (trayPopup is not null) { trayPopup.Dismiss(); return; }
        if (DateTime.UtcNow - TrayPopup.ClosedAt < TimeSpan.FromMilliseconds(400)) return;
        var popup = new TrayPopup(
            () => { if (StopButton.IsEnabled) StopClick(this, new RoutedEventArgs()); else ConnectClick(this, new RoutedEventArgs()); },
            ShowWindow, ExitApplication, PickRow);
        trayPopup = popup;
        popup.Closed += (_, _) => { if (ReferenceEquals(trayPopup, popup)) trayPopup = null; };
        RefreshTrayPopup();
        var area = SystemParameters.WorkArea;
        popup.Left = Math.Max(area.Left, area.Right - popup.Width - 12);
        // Высота — по содержимому (число серверов разное): угол окна ставится, когда она известна.
        popup.Top = area.Bottom - 440;
        popup.Loaded += (_, _) => popup.Top = Math.Max(area.Top, area.Bottom - popup.ActualHeight - 12);
        popup.Show();
    }

    // Трей повторяет главный экран: тот же статус и быстрая смена — автовыбор и три сервера.
    private void RefreshTrayPopup() => trayPopup?.Refresh(Home.StatusTitle.Text,
        ConnectionActive ? Home.StatusSub.Text : Home.ModeText.Text,
        Home.Power.State, Home.StatusCard.Tag as string ?? "Night", Home.Face.Mood, QuickRows().Take(4).ToArray());

    internal TrayPopup CreateTrayPreview()
    {
        var popup = new TrayPopup(() => { }, () => { }, () => { });
        popup.Refresh(Home.StatusTitle.Text, Home.ModeText.Text, Home.Power.State, Home.StatusCard.Tag as string ?? "Night", Home.Face.Mood, QuickRows().Take(4).ToArray());
        return popup;
    }
}

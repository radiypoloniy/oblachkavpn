using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private readonly DispatcherTimer presentationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? sessionStarted;
    private DateTimeOffset? lastCheck;
    private ProbeResult? lastProbe;
    private int? presentedPid;
    private bool findingRoute;

    private void RefreshPresentation()
    {
        if (!viewsReady) return;
        var state = engine.State;
        var pid = engine.ProcessId;
        if (pid != presentedPid)
        {
            presentedPid = pid;
            sessionStarted = pid is null ? null : DateTimeOffset.UtcNow;
            lastProbe = null; lastCheck = null;
        }
        var busy = state is ConnectionState.Starting or ConnectionState.Validating;
        var color = state switch
        {
            ConnectionState.Verified => "#18A577", ConnectionState.Running => "#2E63ED",
            ConnectionState.Failed => "#D14539", _ => busy || findingRoute ? "#E49B19" : "#8A96A8"
        };
        var foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        StatusDot.Fill = foreground;
        StatusText.Foreground = foreground;
        Connection.StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(state switch
        {
            ConnectionState.Verified => "#E6F5EF", ConnectionState.Running => "#ECF1FF",
            ConnectionState.Failed => "#FCEEEB", _ => busy || findingRoute ? "#FFF4E1" : "#F0F2F6"
        }));
        Connection.Headline.Text = findingRoute ? "Ищем маршрут" : state switch
        {
            ConnectionState.Idle => "Туннель выключен", ConnectionState.Verified => "Подключено",
            ConnectionState.Running => "Прокси запущен", ConnectionState.Failed => "Подключение прервано", _ => "Подключаемся"
        };
        Connection.Subtitle.Text = findingRoute ? "Проверим до шести подключений, не дольше двух минут" : state switch
        {
            ConnectionState.Verified => "Запрос через выбранный сервер выполнен",
            ConnectionState.Running => "Локальный вход готов. Проверьте запрос через туннель",
            ConnectionState.Failed => "Попробуйте снова или выберите другой сервер",
            ConnectionState.Idle => "Включите выбранное подключение", _ => "Проверяем профиль и запускаем ядро"
        };
        var canStop = StopButton.IsEnabled;
        ConnectButton.Visibility = canStop ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Background = foreground;
        StopButton.Foreground = foreground;
        Connection.Statistics.Visibility = pid is null ? Visibility.Collapsed : Visibility.Visible;
        Connection.SessionTime.Text = sessionStarted is null ? "0:00:00" : (DateTimeOffset.UtcNow - sessionStarted.Value).ToString(@"h\:mm\:ss");
        Connection.RequestTime.Text = lastProbe is { Success: true } ? $"{lastProbe.Milliseconds} мс" : lastProbe is null ? "Не проверен" : "Не прошёл";
        Connection.CheckTunnelButton.IsEnabled = ProbeButton.IsEnabled;
        MiniStatusText.Text = StatusText.Text;
        MiniStatusText.Foreground = foreground;
        MiniProfile.Text = profile?.DisplayName ?? "Локальный SOCKS5";
        Servers.SelectedName.Text = profile?.DisplayName ?? "Выберите подключение";
        Servers.SelectedTransport.Text = profile?.Transport ?? "—";
        Servers.SelectedCore.Text = profile is null ? "Ядро выберется по формату профиля" : profile.Core == CoreKind.Xray ? "Xray 26.9.9" : "sing-box 1.14.3";
        Servers.UseServerButton.IsEnabled = profile is not null;
        Servers.SearchBox.IsEnabled = ProfilePicker.IsEnabled;
        RefreshServerListControls();
        Diagnostics.CoreState.Text = pid is null ? "Не запущен" : "Готов";
        Diagnostics.TunnelState.Text = lastProbe is null ? "Не проверен" : lastProbe.Success ? $"{lastProbe.Milliseconds} мс" : "Не прошёл";
        Diagnostics.Heading.Text = lastProbe is { Success: true } ? "Туннель проверен" : "Проверим подключение";
        Diagnostics.LastCheck.Text = lastCheck is null ? "Проверка ещё не выполнялась" : "Последняя проверка " + lastCheck.Value.ToLocalTime().ToString("HH:mm:ss");
        Onboarding.ContinueButton.IsEnabled = operation is null;
        Settings.SaveStateButton.IsEnabled = stateStore is not null && profile is not null && operation is null && pendingImport is null;
        Onboarding.KeyInput.IsEnabled = operation is null;
        Onboarding.KeyButton.IsEnabled = PasteButton.IsEnabled;
        Onboarding.FileButton.IsEnabled = ImportFileButton.IsEnabled;
        if (profile is not null && Onboarding.Visibility == Visibility.Visible) Navigate("Connection");
        RefreshTrayPopup();
        RefreshMeasurement();
        FlushStateSave();
    }

    private void RecordProbe(ProbeResult result)
    {
        // Synchronize process identity first so the next refresh cannot erase this result.
        RefreshPresentation();
        lastProbe = result;
        lastCheck = DateTimeOffset.UtcNow;
        RefreshPresentation();
    }
}

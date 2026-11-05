using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Пауза VPN (как у NordVPN и Surfshark): выключить на 5, 15 или 60 минут — для банка или сайта,
// который не пускает из-за границы, — и не забыть включить. Срок — по часам, а не по таймеру:
// после сна компьютера просроченную паузу завершает владелец приложения, даже без окна.
// Тумблер во время паузы включает VPN сразу; «Не включать сам» оставляет его выключенным.
public partial class MainWindow
{
    private DateTimeOffset? pausedUntil => runtime.Pause.Until;
    private static readonly (int Minutes, string Title)[] PauseChoices = [(5, "На 5 минут"), (15, "На 15 минут"), (60, "На час")];

    private void WirePause() => Home.PauseButton.Click += (_, _) =>
    {
        if (pausedUntil is not null) { CancelPause(); return; }
        Home.PauseItems.Children.Clear();
        foreach (var (minutes, title) in PauseChoices)
        {
            var item = new Button { Content = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }, Style = (Style)FindResource("MenuRow") };
            item.Click += (_, _) => { Home.PausePopup.IsOpen = false; StartPause(minutes); };
            Home.PauseItems.Children.Add(item);
        }
        Home.PausePopup.IsOpen = true;
    };

    private void RefreshPauseButton()
    {
        var offered = pausedUntil is not null || ConnectionActive && StopButton.IsEnabled && operation is null && !systemStopping &&
            (systemTunnel is not { PinnedServices.Count: > 0 } || systemTunnel.CanPauseMain);
        Home.PauseButton.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        Home.PauseText.Text = pausedUntil is null ? "Пауза" : "Не включать сам";
        Home.PauseIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, pausedUntil is null ? "IcoPause" : "IcoClose");
    }

    private async void StartPause(int minutes)
    {
        if (!ConnectionActive || !StopButton.IsEnabled || operation is not null) return;
        stoppedOnNetwork = currentNetwork?.Key;
        try { await runtime.Pause.PauseAsync(minutes); if (!closed && pausedUntil is not null) DetailText.Text = "Пауза: VPN включится сам в " + PauseEnd + "."; }
        catch (Exception error) when (error is UserError or IOException or TimeoutException or ObjectDisposedException)
        { if (!closed) DetailText.Text = "Приостановить VPN полностью не удалось. Проверьте состояние подключения."; }
        finally { if (!closed) UpdateControls(); }
    }

    private void CancelPause()
    {
        runtime.Pause.Cancel();
        DetailText.Text = "Пауза снята: VPN останется выключенным.";
        RefreshHome();
    }

    private string PauseEnd => pausedUntil?.ToLocalTime().ToString("HH:mm") ?? "";

    private void PauseChanged() { if (!closed) UpdateControls(); }
    private void PauseResuming()
    {
        if (closed) return;
        stoppedOnNetwork = null;
    }
    private void PauseResumed()
    { if (!closed) { SelectProfile(profile!); Connected(); DetailText.Text = "Пауза кончилась: подключение запущено."; } }
    private void PauseResumeFailed()
    { if (!closed) { DetailText.Text = "Пауза кончилась, но подключиться не удалось. Попробуйте снова."; ScheduleRecovery(); } }
}

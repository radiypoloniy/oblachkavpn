using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Пауза VPN (как у NordVPN и Surfshark): выключить на 5, 15 или 60 минут — для банка или сайта,
// который не пускает из-за границы, — и не забыть включить. Срок — по часам, а не по таймеру:
// после сна компьютера просроченная пауза кончается при первой же секунде обновления окна.
// Тумблер во время паузы включает VPN сразу; «Не включать сам» оставляет его выключенным.
public partial class MainWindow
{
    private DateTimeOffset? pausedUntil;
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
        var offered = pausedUntil is not null || ConnectionActive && StopButton.IsEnabled && !systemStopping;
        Home.PauseButton.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        Home.PauseText.Text = pausedUntil is null ? "Пауза" : "Не включать сам";
        Home.PauseIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, pausedUntil is null ? "IcoPause" : "IcoClose");
    }

    private void StartPause(int minutes)
    {
        if (!ConnectionActive || !StopButton.IsEnabled) return;
        StopClick(this, new RoutedEventArgs());
        // Перезагрузка во время паузы — VPN включится при запуске, как будто паузы не было.
        RememberConnected(true);
        pausedUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);
        DetailText.Text = "Пауза: VPN включится сам в " + PauseEnd + ".";
        RefreshHome();
    }

    private void CancelPause()
    {
        pausedUntil = null;
        RememberConnected(false);
        DetailText.Text = "Пауза снята: VPN останется выключенным.";
        RefreshHome();
    }

    private string PauseEnd => pausedUntil?.ToLocalTime().ToString("HH:mm") ?? "";

    // Каждую секунду (presentationTimer). Пока сессия ещё снимается, ждём следующей секунды.
    private void PollPause()
    {
        if (pausedUntil is not { } until || DateTimeOffset.UtcNow < until) return;
        if (ConnectionActive) { pausedUntil = null; return; }
        if (!ConnectButton.IsEnabled) return;
        pausedUntil = null;
        Notify(Notice.PauseEnded, "Пауза кончилась", "VPN снова включается.");
        ConnectClick(this, new RoutedEventArgs());
    }
}

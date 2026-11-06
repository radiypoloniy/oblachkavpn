using System.Windows;

namespace Oblachka;

public partial class MainWindow : IClientTrayView
{
    internal bool IsClosed => closed;
    bool IClientTrayView.Closed => closed;
    bool IClientTrayView.Foreground => IsVisible && IsActive && WindowState != WindowState.Minimized;
    void IClientTrayView.Handle(TrayCommand command, string? renewUrl)
    {
        if (closed) return;
        switch (command)
        {
            case TrayCommand.Open: ShowWindow(); break;
            case TrayCommand.Connect: ConnectClick(this, new RoutedEventArgs()); break;
            case TrayCommand.Stop: StopClick(this, new RoutedEventArgs()); break;
            case TrayCommand.Exit: ExitApplication(); break;
            case TrayCommand.Popup: ShowTrayPopup(); break;
            case TrayCommand.Notice:
                if (renewUrl is null) ShowWindow();
                else OpenLink(renewUrl, "страницу продления");
                break;
            case TrayCommand.Update: ShowWindow(); OpenSettings(Views.SettingsPanel.TransferPage, fromHub: true); break;
        }
    }
}

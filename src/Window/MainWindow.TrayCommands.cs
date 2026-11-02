using System.Windows;

namespace Oblachka;

public partial class MainWindow : IClientTrayView
{
    internal bool IsClosed => closed;
    bool IClientTrayView.Closed => closed;
    void IClientTrayView.Handle(TrayCommand command)
    {
        if (closed) return;
        switch (command)
        {
            case TrayCommand.Open: ShowWindow(); break;
            case TrayCommand.Connect: ConnectClick(this, new RoutedEventArgs()); break;
            case TrayCommand.Stop: StopClick(this, new RoutedEventArgs()); break;
            case TrayCommand.Exit: ExitApplication(); break;
            case TrayCommand.Popup: ShowTrayPopup(); break;
            case TrayCommand.Notice: NoticeClicked(); break;
            case TrayCommand.Update: ShowWindow(); OpenSettings(Views.SettingsPanel.AppPage); break;
        }
    }
}

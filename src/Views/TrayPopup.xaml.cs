using System.Windows;

namespace Oblachka.Views;

public partial class TrayPopup : Window
{
    private bool dismissing;
    private bool finished;
    public TrayPopup(Action power, Action open, Action exit)
    {
        InitializeComponent();
        PowerButton.Click += (_, _) => power();
        OpenButton.Click += (_, _) => { Dismiss(); open(); };
        ExitButton.Click += (_, _) => { Dismiss(); exit(); };
        Deactivated += (_, _) => Dismiss();
        Closing += (_, _) => dismissing = true;
        Closed += (_, _) => finished = true;
    }

    internal void Dismiss()
    {
        if (dismissing || finished) return;
        dismissing = true;
        Close();
    }

    public void Refresh(string state, string profile, string transport, string check, string session, bool active, bool enabled)
    {
        StateText.Text = state;
        ProfileText.Text = profile;
        TransportText.Text = transport;
        CheckText.Text = check;
        SessionText.Text = session;
        PowerButton.Content = active ? "Остановить" : "Включить";
        PowerButton.ToolTip = PowerButton.Content;
        PowerButton.HorizontalContentAlignment = active ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        PowerButton.Background = active ? (System.Windows.Media.Brush)FindResource("Green") : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(227, 231, 237));
        PowerButton.IsEnabled = enabled;
    }
}

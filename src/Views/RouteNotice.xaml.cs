using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Oblachka.Views;

public partial class RouteNotice : Window
{
    private readonly DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(12) };

    internal RouteNotice(string route, string message, Action openRule)
    {
        InitializeComponent();
        Face.Mood = CloudMood.Worried;
        RouteName.Text = route;
        Message.Text = message;
        CloseButton.Click += (_, _) => Close();
        DismissButton.Click += (_, _) => Close();
        OpenButton.Click += (_, _) => { Close(); openRule(); };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        };
        timeout.Tick += (_, _) =>
        {
            if (!IsMouseOver && !IsActive) Close();
        };
        Closed += (_, _) => timeout.Stop();
    }

    internal void ShowAtTray()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width);
        Left = Math.Max(area.Left, area.Right - Width - 8);
        Loaded += (_, _) => Top = Math.Max(area.Top, area.Bottom - ActualHeight - 8);
        Show();
        timeout.Start();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;

namespace Oblachka.Views;

public partial class TrayPopup : Window
{
    private bool dismissing;
    private bool finished;

    public TrayPopup(Action power, Action open, Action exit, Action<ServerRow>? pick = null)
    {
        InitializeComponent();
        PowerButton.Click += (_, _) => power();
        OpenButton.Click += (_, _) => { Dismiss(); open(); };
        ExitButton.Click += (_, _) => { Dismiss(); exit(); };
        QuickList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, args) =>
        { if ((args.OriginalSource as FrameworkElement)?.DataContext is ServerRow row) pick?.Invoke(row); }));
        // Закрывается щелчком мимо (окно активируется при показе), Esc и повторным щелчком по значку.
        Deactivated += (_, _) => Dismiss();
        KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) Dismiss(); };
        Loaded += (_, _) => Activate();
        Closing += (_, _) => dismissing = true;
        Closed += (_, _) => { finished = true; ClosedAt = DateTime.UtcNow; };
    }

    // Щелчок по значку сначала снимает фокус с окна, и оно закрывается; тот же щелчок
    // не должен открыть его снова.
    public static DateTime ClosedAt { get; private set; }

    internal void Dismiss()
    {
        if (dismissing || finished) return;
        dismissing = true;
        Close();
    }

    public void Refresh(string state, string sub, bool active, bool enabled, IReadOnlyList<ServerRow> rows)
    {
        StateText.Text = state;
        SubText.Text = sub;
        PowerButton.SetResourceReference(BackgroundProperty, active ? "Ac" : "Off");
        Knob.Margin = new Thickness(active ? 34 : 4, 0, 0, 0);
        AutomationProperties.SetName(PowerButton, active ? "Выключить" : "Включить");
        PowerButton.IsEnabled = enabled;
        // Обновление идёт раз в секунду: список меняется, только когда изменилось видимое.
        var signature = string.Join('\n', rows.Select(r => r.Name + "|" + r.Selected + "|" + r.Latency));
        if (signature != rowsSignature) { rowsSignature = signature; QuickList.ItemsSource = rows; }
    }

    private string? rowsSignature;
}

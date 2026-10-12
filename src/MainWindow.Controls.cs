using System.Windows.Controls;
using System.Windows.Shapes;

namespace RoutePilot;

// View-local controls are exposed here to keep existing operation ownership in MainWindow.
public partial class MainWindow
{
    private Button ConnectButton => Connection.ConnectButton;
    private Button StopButton => Connection.StopButton;
    private TextBlock StatusText => Connection.StatusText;
    private Ellipse StatusDot => Connection.StatusDot;
    private TextBlock ProfileNameText => Connection.ProfileNameText;
    private TextBlock TransportText => Connection.TransportText;
    private ListBox ProfilePicker => Servers.ProfilePicker;
    private Button ImportFileButton => Servers.ImportFileButton;
    private Button PasteButton => Servers.PasteButton;
    private Button SubscriptionButton => Servers.SubscriptionButton;
    private Button RefreshButton => Servers.RefreshButton;
    private Button ProbeButton => Diagnostics.ProbeButton;
    private Button FindRouteButton => Diagnostics.FindRouteButton;
    private Button ReconnectButton => Diagnostics.ReconnectButton;
    private TextBox ProbeUrl => Diagnostics.ProbeUrl;
    private TextBlock EventText => Diagnostics.EventText;
    private TextBlock DetailText => ShellFeedback;
    private CheckBox AutoRecover => Settings.AutoRecover;
    private Button CopyEndpointButton => Routes.CopyEndpointButton;
    private TextBlock EndpointText => Routes.EndpointText;

    private void WireViews()
    {
        WireRules();
        WireVmTun();
        WireMeasurements();
        ConnectButton.Click += ConnectClick;
        StopButton.Click += StopClick;
        ProfilePicker.SelectionChanged += ProfileSelectionChanged;
        ImportFileButton.Click += ImportFileClick;
        PasteButton.Click += PasteClick;
        SubscriptionButton.Click += SubscriptionClick;
        RefreshButton.Click += RefreshClick;
        ProbeButton.Click += ProbeClick;
        FindRouteButton.Click += FindRouteClick;
        ReconnectButton.Click += ReconnectClick;
        CopyEndpointButton.Click += CopyEndpointClick;
        Diagnostics.ExportButton.Click += ExportReportClick;
        Connection.ChangeServerButton.Click += (_, _) => Navigate("Servers");
        Connection.RoutesButton.Click += (_, _) => Navigate("Routes");
        Connection.CheckTunnelButton.Click += (_, _) => Navigate("Diagnostics");
        Servers.UseServerButton.Click += (_, _) => Navigate("Connection");
        Servers.SearchBox.TextChanged += (_, _) => FilterProfiles();
        Onboarding.KeyButton.Click += PasteClick;
        Onboarding.FileButton.Click += ImportFileClick;
        Onboarding.ContinueButton.Click += ContinueSubscription;
        Onboarding.RememberSubscription.Checked += (_, _) => rememberConnections = true;
        Onboarding.RememberSubscription.Unchecked += (_, _) => rememberConnections = false;
        Settings.StayInTray.Click += (_, _) => QueueStateSave();
        AutoRecover.Click += (_, _) => QueueStateSave();
        Settings.AutoRefresh.Click += (_, _) => QueueStateSave();
        Settings.CloseButton.Click += (_, _) => CloseSettings();
        Settings.ManageSubscriptionButton.Click += (_, _) => { CloseSettings(); Navigate("Servers"); };
        Settings.SaveStateButton.Click += SaveStateClick;
        Settings.OpenDiagnosticsButton.Click += (_, _) => { CloseSettings(); Navigate("Diagnostics"); };
    }
}

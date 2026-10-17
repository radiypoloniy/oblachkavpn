using System.Windows.Controls;
using System.Windows.Shapes;

namespace Oblachka;

// View-local controls are exposed here to keep existing operation ownership in MainWindow.
public partial class MainWindow
{
    // Держатели состояния подключения вне дерева элементов. Логика окна выставляет им
    // IsEnabled и Text, как прежним кнопкам; новый главный экран (MainWindow.Home) читает их
    // и рисует один тумблер вместо двух кнопок.
    private readonly Button ConnectButton = new();
    private readonly Button StopButton = new();
    private readonly TextBlock StatusText = new();
    private readonly Ellipse StatusDot = new();
    private readonly TextBlock ProfileNameText = new();
    private readonly TextBlock TransportText = new();
    // Модель выбора сервера: прежний список, теперь вне дерева элементов. Его выбор и событие
    // SelectionChanged ведут смену сервера и сохранение; рисуют список главный экран и панель.
    private readonly ListBox ProfilePicker = new();
    // Правила и помощь теперь вкладки настроек; имена элементов прежние.
    private Views.RulesTab Routes => RulesPage;
    private Views.BypassPage Bypass => Settings.Bypass;
    private Views.GeneralTab AppTab => Settings.General;
    private Views.AdvancedPage Advanced => Settings.Advanced;
    private Views.HelpTab Diagnostics => Settings.Help;
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
    private Button CopyEndpointButton => Advanced.CopyEndpointButton;
    private TextBlock EndpointText => Advanced.EndpointText;

    private void WireViews()
    {
        WireRules();
        WireVmTun();
        WireMeasurements();
        WireServerList();
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
        Diagnostics.CopyReportButton.Click += CopyReportClick;
        WireHome();
        WireSettings();
        Servers.BackButton.Click += (_, _) => Navigate("Connection");
        Servers.SearchBox.TextChanged += (_, _) => FilterProfiles();
        Onboarding.FileButton.Click += ImportFileClick;
        WireQr();
        WireDoctor();
        WireSiteCheck();
        WireUpdates();
        WireDpi();
        WireZapret();
        Onboarding.ContinueButton.Click += ContinueSubscription;
        Onboarding.ForeignList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new System.Windows.RoutedEventHandler(ForeignClick));
        Settings.StayInTray.Click += (_, _) => QueueStateSave();
        AutoRecover.Click += (_, _) => QueueStateSave();
        Settings.AutoRefresh.Click += (_, _) => QueueStateSave();
        Settings.CloseButton.Click += (_, _) => { if (!Settings.Back()) CloseSettings(); };
        WireSettingsHome();
        Settings.AddExternalButton.Click += AddExternalClick;
        Settings.Subscription.GroupList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new System.Windows.RoutedEventHandler(GroupRowClick));
        Servers.GroupTabs.SelectionChanged += (_, _) => FilterProfiles();
    }
}

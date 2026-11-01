using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Oblachka;

public partial class MainWindow
{
    private ClientPreferences preferences => runtime.Preferences;
    private void BindPreferences()
    {
        void Bind(CheckBox control, string name) => control.SetBinding(ToggleButton.IsCheckedProperty,
            new Binding(name) { Source = preferences, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Bind(Settings.StayInTray, nameof(ClientPreferences.StayInTray));
        Bind(Settings.AutoRecover, nameof(ClientPreferences.AutoRecover));
        Bind(Settings.AutoRefresh, nameof(ClientPreferences.AutoRefresh));
        Bind(Settings.ConnectOnLaunch, nameof(ClientPreferences.ConnectOnLaunch));
        Bind(Settings.Notices, nameof(ClientPreferences.Notices));
        Bind(Settings.LocalNetwork, nameof(ClientPreferences.LocalNetwork));
        Bind(Settings.QuicOverTcp, nameof(ClientPreferences.QuicOverTcp));
        Bind(Settings.CompatibleStack, nameof(ClientPreferences.CompatibleStack));
        Bind(Settings.Hotkey, nameof(ClientPreferences.Hotkey));
        Bind(Settings.SystemProxy, nameof(ClientPreferences.SystemProxy));
        Bind(Settings.SystemTunnel, nameof(ClientPreferences.SystemTunnel));
        Bind(Settings.RussiaDirect, nameof(ClientPreferences.RussiaDirect));
        Bind(AppTab.UpdateAuto, nameof(ClientPreferences.UpdateAuto));
        Bind(AppTab.UpdatePrerelease, nameof(ClientPreferences.UpdatePrerelease));
        Bind(AppTab.SecureUnknown, nameof(ClientPreferences.SecureUnknown));
        Bind(AppTab.Lockdown, nameof(ClientPreferences.Lockdown));
    }
}

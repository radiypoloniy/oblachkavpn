using System.Windows.Interop;

namespace Oblachka;

// Дополнительные горячие клавиши («Для продвинутых»), по умолчанию выключены: Ctrl+Alt+V —
// включить или выключить VPN (как кнопка в трее), Ctrl+Alt+B — обход блокировок. Занятое
// другой программой сочетание не берётся, и на странице так и написано.
public partial class MainWindow
{
    private const int VpnHotkeyId = 0x4F43, BypassHotkeyId = 0x4F44;
    private const uint KeyV = 0x56, KeyB = 0x42;
    private bool vpnHotkey, bypassHotkey;

    private void ApplyExtraHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || isolatedPersistence) return;
        if (vpnHotkey) UnregisterHotKey(handle, VpnHotkeyId);
        if (bypassHotkey) UnregisterHotKey(handle, BypassHotkeyId);
        vpnHotkey = advanced.VpnHotkey && RegisterHotKey(handle, VpnHotkeyId, ModControl | ModAlt | ModNoRepeat, KeyV);
        bypassHotkey = advanced.BypassHotkey && RegisterHotKey(handle, BypassHotkeyId, ModControl | ModAlt | ModNoRepeat, KeyB);
        Advanced.HotkeyNote.Text = advanced.VpnHotkey && !vpnHotkey || advanced.BypassHotkey && !bypassHotkey
            ? "Одно из сочетаний занято другой программой — Облачка его не берёт"
            : "Действуют из любой программы, даже когда окно спрятано в трей";
    }

    private void ReleaseExtraHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (vpnHotkey) UnregisterHotKey(handle, VpnHotkeyId);
        if (bypassHotkey) UnregisterHotKey(handle, BypassHotkeyId);
    }

    // Нажатие: true — сочетание наше и обработано.
    private bool ExtraHotkey(int id)
    {
        if (id == VpnHotkeyId)
        {
            if (StopButton.IsEnabled) StopClick(this, new System.Windows.RoutedEventArgs());
            else if (ConnectButton.IsEnabled) ConnectClick(this, new System.Windows.RoutedEventArgs());
            return true;
        }
        if (id != BypassHotkeyId) return false;
        Home.BypassToggle.IsChecked = Home.BypassToggle.IsChecked != true;
        Home.BypassToggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        return true;
    }
}

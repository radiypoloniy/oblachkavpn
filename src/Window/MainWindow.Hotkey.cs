using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Oblachka;

// Горячая клавиша Ctrl+Alt+O: окно из трея из любой программы, а если оно уже впереди — обратно
// в трей. Сочетание занято другой программой — RegisterHotKey откажет, и мы его не берём, а в
// настройках так и пишем. Проверочные запуски окна сочетание не трогают: его держит настоящее.
public partial class MainWindow
{
    private const int HotkeyId = 0x4F42, WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, KeyO = 0x4F;
    private bool hotkeyRegistered;

    private void InitHotkey()
    {
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(HotkeyHook);
            ApplyHotkey(); ApplyExtraHotkeys();
        };
        Settings.Hotkey.Click += (_, _) => { ApplyHotkey(); QueueStateSave(); };
        Closed += (_, _) => { if (hotkeyRegistered) UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId); ReleaseExtraHotkeys(); };
    }

    private void ApplyHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || isolatedPersistence) return;
        if (hotkeyRegistered) UnregisterHotKey(handle, HotkeyId);
        var wanted = Settings.Hotkey.IsChecked == true;
        hotkeyRegistered = wanted && RegisterHotKey(handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, KeyO);
        Settings.HotkeyNote.Text = wanted && !hotkeyRegistered
            ? "Сочетание занято другой программой — Облачка его не берёт"
            : "Из любой программы; нажмите ещё раз — окно уйдёт в трей";
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        if (ExtraHotkey(wParam.ToInt32())) { handled = true; return IntPtr.Zero; }
        if (wParam.ToInt32() != HotkeyId) return IntPtr.Zero;
        handled = true;
        if (IsVisible && IsActive && WindowState != WindowState.Minimized) HideToTray();
        else ShowWindow();
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

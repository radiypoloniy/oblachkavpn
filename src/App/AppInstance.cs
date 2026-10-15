using Microsoft.Win32;

namespace Oblachka;

// Один экземпляр на папку сборки и автозапуск вместе с Windows.
//
// Крестик по умолчанию прячет окно в трей, поэтому повторный запуск с ярлыка — обычное
// действие «открыть Облачка», а не ошибка: второй экземпляр будит первый и выходит.
public static class AppInstance
{
    public const string TrayArgument = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string Suffix => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant())))[..16];
    public static string MutexName => "Local\\Oblachka-" + Suffix;

    // Второй экземпляр просит первый показать окно.
    public static void SignalFirst()
    {
        try { using var show = EventWaitHandle.OpenExisting("Local\\Oblachka-show-" + Suffix); show.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    // Первый экземпляр ждёт просьб показать окно, пока жив процесс.
    public static void ListenForShow(Action show)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Oblachka-show-" + Suffix);
        new Thread(() => { while (signal.WaitOne()) show(); }) { IsBackground = true, Name = "show-window" }.Start();
    }

    // Автозапуск — запись текущего пользователя: прав администратора не нужно, и включает
    // его сам человек в настройках. Стартует сразу в трей.
    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ServiceBrand.LatinName) is string value &&
                   value.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ServiceBrand.LatinName, "\"" + Environment.ProcessPath + "\" " + TrayArgument);
            else key.DeleteValue(ServiceBrand.LatinName, throwOnMissingValue: false);
        }
    }
}

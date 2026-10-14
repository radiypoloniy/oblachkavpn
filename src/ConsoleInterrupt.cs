using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Oblachka;

// Мягкая остановка консольного ядра: Ctrl+Break в его консоль, как при ручном запуске.
//
// Ядро запускается без окна, но с собственной скрытой консолью и в своей группе
// процессов (CreateNewProcessGroup). Помощник — оконное приложение без консоли: он
// ненадолго подключается к консоли ядра и шлёт событие только группе ядра.
//
// Событие всей консоли (группа 0) пробовать нельзя: так оно достаётся и отправителю, и
// на стенде помощник от него погиб посреди остановки. Ctrl+C тоже не годится: в новой
// группе процессов он отключён. Go-ядро получает Ctrl+Break как прерывание, штатно
// закрывается (код 0) и удаляет свой адаптер.
internal static class ConsoleInterrupt
{
    private const uint CtrlBreak = 1;
    private static readonly object Gate = new();

    // true — процесс завершился сам за отведённое время.
    public static bool Send(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited) return true;
            lock (Gate)
            {
                // Своя консоль у помощника быть не должна; если она есть, не трогаем её.
                if (!AttachConsole((uint)process.Id)) return false;
                try { if (!GenerateConsoleCtrlEvent(CtrlBreak, (uint)process.Id)) return false; }
                finally { FreeConsole(); }
            }
            return process.WaitForExit(timeout);
        }
        catch (InvalidOperationException) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}

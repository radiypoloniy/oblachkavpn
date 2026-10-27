using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Oblachka;

// Состояние, установка и удаление сетевой службы.
//
// Установка и удаление — дело установщика (и проверочных скриптов): им нужны права
// администратора. Окно только спрашивает состояние: это разрешено любому пользователю.
public static class NetworkServiceControl
{
    public const string InstallArgument = "--install-service";
    public const string UninstallArgument = "--uninstall-service";

    public sealed record Status(bool Installed, bool Running, int ProcessId);

    public static Status Query()
    {
        var manager = OpenSCManager(null, null, 0x1); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero) return new(false, false, 0);
        try
        {
            var service = OpenService(manager, NetworkService.Name, 0x4); // SERVICE_QUERY_STATUS
            if (service == IntPtr.Zero) return new(false, false, 0);
            try
            {
                var buffer = new int[9]; // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(service, 0, buffer, buffer.Length * 4, out _)) return new(true, false, 0);
                var running = buffer[1] == 4; // SERVICE_RUNNING
                return new(true, running, running ? buffer[7] : 0);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    // Служба работает от SYSTEM, поэтому её файл должен лежать там, куда без прав
    // администратора не пишут. Иначе любая программа пользователя подменила бы Oblachka.exe
    // и получила SYSTEM.
    public static bool IsProtectedLocation(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(f => Environment.GetFolderPath(f)).Where(p => p.Length > 0)
            .Any(p => full.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    // 0 — готово, 2 — нет прав, 3 — сборка не в Program Files, 4 — отказ диспетчера служб.
    public static int Install()
    {
        if (!NetworkHelperHost.IsElevated()) return 2;
        if (!IsProtectedLocation(AppContext.BaseDirectory)) return 3;
        if (Query().Installed && Uninstall() != 0) return 4;
        var executable = Path.Combine(AppContext.BaseDirectory, "Oblachka.exe");
        var steps = new[]
        {
            new[] { "create", NetworkService.Name, "binPath=", "\"" + executable + "\" " + NetworkService.Argument,
                "start=", "auto", "DisplayName=", ServiceBrand.Name + " — сетевая служба" },
            new[] { "description", NetworkService.Name, "Системный режим " + ServiceBrand.Name + ": туннель и защита от трафика мимо него." },
            // Упавшая служба перезапускается: при старте она снимает оставшуюся сессию.
            new[] { "failure", NetworkService.Name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000" },
            new[] { "start", NetworkService.Name }
        };
        foreach (var step in steps) if (Sc(step) != 0) return 4;
        return WaitFor(s => s.Running) ? 0 : 4;
    }

    // Остановка службы снимает сессию и возвращает обычную сеть, затем служба удаляется.
    // Запрет без VPN постоянный и без Облачки не снялся бы никогда — его снимаем первым. При обновлении
    // установщик зовёт то же самое; запрет ставит снова окно новой версии при запуске.
    public static int Uninstall()
    {
        if (!NetworkHelperHost.IsElevated()) return 2;
        try { using var engine = WfpEngine.Open(); WfpLockdown.Remove(engine); }
        catch (IOException) { } // служба фильтрации недоступна — тогда и запрет не действует
        if (!Query().Installed) return 0;
        Sc("stop", NetworkService.Name);
        if (!WaitFor(s => !s.Running)) return 4;
        return Sc("delete", NetworkService.Name) == 0 && WaitFor(s => !s.Installed) ? 0 : 4;
    }

    private static bool WaitFor(Func<Status, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline) { if (condition(Query())) return true; Thread.Sleep(250); }
        return condition(Query());
    }

    private static int Sc(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, int access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string name, int access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, int[] buffer, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

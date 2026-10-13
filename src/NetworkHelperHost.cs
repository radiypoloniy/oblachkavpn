using System.Diagnostics;
using System.Security.Principal;

namespace Oblachka;

// Привилегированная половина системного режима: отдельный процесс с правами администратора,
// который владеет сетевой сессией и ядром туннеля. Интерфейс приложения прав не требует.
//
// Из командной строки принимаются ровно два значения: имя канала и идентификатор владельца.
// Ни пути к исполняемым файлам, ни конфигурации, ни адресов узлов здесь нет — всё это
// приходит по проверенному контракту IPC либо выводится из собственной папки сборки.
public static class NetworkHelperHost
{
    public const string Argument = "--network-helper";

    public sealed record Arguments(string PipeName, int OwnerProcessId);

    public static Arguments ParseArguments(string[] args)
    {
        if (args.Length != 3 || args[0] != Argument) throw new UserError("Неверные аргументы сетевого помощника.");
        HelperPipeIdentity.ValidateName(args[1]);
        if (!int.TryParse(args[2], out var owner) || owner <= 0) throw new UserError("Неверный владелец сетевого помощника.");
        return new(args[1], owner);
    }

    public static string JournalPath => Path.Combine(ClientDataPaths.Root, "network-session.json");
    public static string FailurePath => Path.Combine(ClientDataPaths.Root, "helper-failure.txt");
    public static string TracePath => Path.Combine(ClientDataPaths.Root, "helper-trace.txt");

    // Отметки шагов сессии: время UTC и вид изменения. Файл не растёт без предела.
    internal static void RecordStep(string line)
    {
        try
        {
            Directory.CreateDirectory(ClientDataPaths.Root);
            if (File.Exists(TracePath) && new FileInfo(TracePath).Length > 64 * 1024) File.Delete(TracePath);
            File.AppendAllText(TracePath, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException) { }
    }

    // Записывается только тип и сообщение ожидаемого отказа: ни ключей, ни адресов узлов
    // в таких сообщениях нет. Запись не должна мешать завершению, поэтому ошибки записи
    // проглатываются.
    internal static void RecordFailure(Exception ex)
    {
        try
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex.GetType().Name + ": " + ex.Message;
            Directory.CreateDirectory(ClientDataPaths.Root);
            File.WriteAllText(FailurePath, line, new System.Text.UTF8Encoding(true));
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException) { }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Возвращаемый код: 0 — штатное завершение, 2 — отказ. Текст наружу не печатается:
    // в сообщениях могли бы оказаться адреса узлов.
    public static int Run(string[] args, Func<bool>? elevated = null, Func<string, ITunnelProcess>? core = null)
    {
        Arguments parsed;
        try { parsed = ParseArguments(args); }
        catch (UserError) { return 2; }
        if (!(elevated ?? IsElevated)()) return 2;

        Process owner;
        try { owner = Process.GetProcessById(parsed.OwnerProcessId); }
        catch (ArgumentException) { return 2; }

        using (owner)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe");
            var start = core is null
                ? new Func<ValidatedNetworkLease, CancellationToken, ITunnelProcess>((lease, token) => SingBoxTunnelProcess.Start(lease, executable, token))
                : ((lease, _) => core(executable));
            using var tunnel = new WindowsTunnelBackend(start, TunnelInterfaces.Exists);
            // Брандмауэр открывается один раз на весь запуск помощника. Если его нет,
            // режим работает без блокировки, а снять чужую блокировку будет нечем —
            // восстановление сообщит об этом вместо того, чтобы молчать.
            KillSwitchBackend? killSwitch = null;
            try { killSwitch = new KillSwitchBackend(WindowsFirewallPolicy.Open()); }
            catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException) { }
            // Служба фильтрации проверяется открытием движка: если её нет, защита остаётся
            // за брандмауэром. Движок открывается заново на каждое изменение.
            WfpProtectionBackend? wfp = null;
            try { using (WfpEngine.Open()) { } wfp = new WfpProtectionBackend(WfpEngine.Open); }
            catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException) { }
            using var backend = new SystemNetworkBackend(tunnel, killSwitch, wfp);
            using var journal = new NetworkSessionJournal(JournalPath);
            var session = new NetworkSession(journal, backend, RecordStep);
            var server = new HelperPipeServer(session, parsed.PipeName, owner, report: RecordFailure);
            try
            {
                server.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex) when (ex is UserError or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // Помощник завершается тихо, и со стороны окна это неотличимо от обрыва.
                // Причина записывается рядом с журналом: без неё разбор отказа на чужой
                // машине сводится к догадкам. Адресов узлов в этих сообщениях нет.
                RecordFailure(ex);
                return 2;
            }
        }
    }
}

// Запуск привилегированной половины. Повышение прав запрашивается явно у пользователя:
// окно приложения продолжает работать без прав администратора.
public static class NetworkHelperLauncher
{
    public static Process Launch(string pipeName, int ownerProcessId)
    {
        HelperPipeIdentity.ValidateName(pipeName);
        if (ownerProcessId <= 0) throw new UserError("Неверный владелец сетевого помощника.");
        var start = Describe(pipeName, ownerProcessId);
        try
        {
            return Process.Start(start) ?? throw new UserError("Не удалось запустить сетевой помощник.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 — пользователь отказал в повышении прав. Это обычный ответ, не сбой.
            throw new UserError("Системный режим не включён: запрос прав администратора отклонён.");
        }
    }

    // Отдельно от запуска, чтобы состав команды можно было проверить без запуска процесса.
    internal static ProcessStartInfo Describe(string pipeName, int ownerProcessId)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Oblachka.exe"))
        {
            UseShellExecute = true,          // Verb работает только через оболочку.
            Verb = "runas",                  // Явный запрос прав администратора.
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(NetworkHelperHost.Argument);
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(ownerProcessId.ToString());
        return start;
    }
}

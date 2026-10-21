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

    // Помощник с UAC пишет рядом с данными пользователя, служба — в ProgramData (UseDataRoot).
    private static string root = ClientDataPaths.Root;
    internal static void UseDataRoot(string path) => root = path;
    // Журнал читает и окно: по нему оно узнаёт брошенную сессию. Остальное (правила «напрямую»,
    // программы, списки, трассировка) — в закрытой папке: у службы её читают только SYSTEM и
    // администраторы, а не все пользователи компьютера (аудит 19.10, С-3).
    public const string PrivateFolder = "private";
    public static string JournalPath => Path.Combine(root, "network-session.json");
    internal static string DataPath(string name) => Path.Combine(Private(), name);
    public static string FailurePath => Path.Combine(Private(), "helper-failure.txt");
    public static string TracePath => Path.Combine(Private(), "helper-trace.txt");
    private static string Private() => Directory.CreateDirectory(Path.Combine(root, PrivateFolder)).FullName;

    // Отметки шагов сессии: время UTC и вид изменения. Файл не растёт без предела.
    internal static void RecordStep(string line)
    {
        try
        {
            Directory.CreateDirectory(root);
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
            Directory.CreateDirectory(root);
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
            using var host = CreateSession(core);
            var server = new HelperPipeServer(host.Session, parsed.PipeName, owner, report: RecordFailure);
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

    // Сетевая сессия с настоящими backend: общая для помощника с UAC и для службы.
    internal static SessionHost CreateSession(Func<string, ITunnelProcess>? core)
    {
        var executable = TunCore.Program();
        var start = core is null
            ? new Func<ValidatedNetworkLease, CancellationToken, ITunnelProcess>((lease, token) => SingBoxTunnelProcess.Start(lease, executable, token))
            : ((lease, _) => core(executable));
        var tunnel = new WindowsTunnelBackend(start, TunnelInterfaces.Exists, interfaceUp: TunnelInterfaces.IsUp);
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
        var backend = new SystemNetworkBackend(tunnel, killSwitch, wfp);
        var journal = new NetworkSessionJournal(JournalPath);
        return new(new NetworkSession(journal, backend, RecordStep), tunnel, backend, journal);
    }

    internal sealed class SessionHost(NetworkSession session, params IDisposable[] resources) : IDisposable
    {
        public NetworkSession Session => session;
        public void Dispose() { foreach (var resource in resources.Reverse()) resource.Dispose(); }
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

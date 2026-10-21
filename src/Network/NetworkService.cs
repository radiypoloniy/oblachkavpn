using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Oblachka;

// Сетевая служба Windows: тот же владелец сетевой сессии, что помощник с UAC, но постоянный.
//
// Зачем: помощник жил вместе с окном, и аварийное завершение окна снимало защиту — ровно
// тогда, когда она нужнее всего. Служба при потере окна сессию оставляет: туннель без ядра
// ничего не несёт, фильтры держат сеть закрытой до нового запуска окна («Восстановить сеть»
// или новое подключение), остановки службы или перезагрузки. Заодно не нужен запрос прав
// при каждом подключении.
//
// Служба работает от SYSTEM, поэтому её файлы обязаны лежать там, куда пишет только
// администратор: регистрация разрешена лишь для копии в Program Files (NetworkServiceControl).
// Журнал — в ProgramData с собственными правами: пользователь читает, писать не может.
public static class NetworkService
{
    public const string Name = "OblachkaNetwork";
    public const string Argument = "--network-service";
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Oblachka");
    public static string JournalPath => Path.Combine(DataRoot, "network-session.json");

    private const int StatePending = 2, StateStopPending = 3, StateRunning = 4, StateStopped = 1;
    private static readonly ServiceMainProc MainProc = ServiceMain;
    private static readonly HandlerProc Handler = OnControl;
    private static IntPtr statusHandle;
    private static readonly CancellationTokenSource Stopping = new();
    // Выход из сеанса Windows (SESSIONCHANGE): сеанс окна-владельца и сигнал, будящий цикл службы.
    private static readonly object WakeGate = new();
    private static CancellationTokenSource wake = new();
    private static int ownerSession = -1, loggedOffSession = -1;
    // Пробуждение (POWEREVENT): окно живо — ничего не делаем; его процесса нет — как после перезагрузки.
    // Судим по процессу, а не по каналу: после сна канал может оборваться по сроку пульса у живого окна.
    private static volatile bool resumed;
    private static int ownerPid;
    private static DateTime ownerStart;

    // Вызывается диспетчером служб; из обычного запуска StartServiceCtrlDispatcher откажет.
    public static int Run()
    {
        var table = new[] { new TableEntry { Name = Name, Main = MainProc }, new TableEntry() };
        return StartServiceCtrlDispatcher(table) ? 0 : 2;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        statusHandle = RegisterServiceCtrlHandlerEx(Name, Handler, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero) return;
        Report(StatePending, 0);
        var exit = 0;
        try
        {
            NetworkHelperHost.UseDataRoot(SecureDataRoot());
            Report(StateRunning, 0);
            Serve(Stopping.Token);
        }
        catch (Exception ex) { NetworkHelperHost.RecordFailure(ex); exit = 1; }
        Report(StateStopped, exit);
    }

    private static void Serve(CancellationToken token)
    {
        using var host = NetworkHelperHost.CreateSession(null);
        var session = host.Session;
        // Сессия прошлого запуска (перезагрузка, падение службы) снимается до первого клиента.
        // Отказ не роняет службу: клиент повторит восстановление командой Recover.
        try { session.Recover(); }
        catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException) { NetworkHelperHost.RecordFailure(ex); }
        try
        {
            while (!token.IsCancellationRequested)
            {
                ReleaseLoggedOffOwner(session);
                CancellationTokenSource cycle;
                lock (WakeGate) { if (wake.IsCancellationRequested) { wake.Dispose(); wake = new(); } cycle = wake; }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cycle.Token);
                try
                {
                    using var pipe = HelperPipeIdentity.CreateServiceServer();
                    pipe.WaitForConnectionAsync(linked.Token).GetAwaiter().GetResult();
                    using var client = HelperPipeIdentity.ServiceClient(pipe, AppContext.BaseDirectory);
                    Volatile.Write(ref ownerSession, client.SessionId);
                    ownerPid = client.Id; ownerStart = client.StartTime;
                    HelperPipeServer.ServeAsync(session, pipe, client, null, null, NetworkHelperHost.RecordFailure, linked.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                // Разрыв канала, молчание клиента и его завершение — обычный конец обслуживания.
                // Сессия клиента при этом остаётся: её снимет следующий клиент или остановка службы.
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                catch (Exception ex) when (ex is UserError or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    NetworkHelperHost.RecordFailure(ex);
                    token.WaitHandle.WaitOne(500);
                }
            }
        }
        finally
        {
            // Остановка службы (в том числе при удалении) возвращает обычную сеть и гасит zapret.
            ZapretRunner.Stop();
            session.Recover();
        }
    }

    // Выход из сеанса окна — не авария, а конец работы: сеть возвращается, как при выходе через трей.
    // Без этого «Завершение работы» с быстрым запуском (гибернация ядра, служба не перезапускается)
    // оставляло сессию: после включения туннель без ядра окна и фильтры держали интернет закрытым,
    // пока человек не нажмёт тумблер. Аварийное завершение окна по-прежнему сессию оставляет.
    // Второй путь к тому же — пробуждение без окна: если уведомление о выходе не дошло, гибернация
    // быстрого запуска всё равно кончается пробуждением, а окна того сеанса уже нет.
    private static void ReleaseLoggedOffOwner(NetworkSession session)
    {
        var loggedOff = Interlocked.Exchange(ref loggedOffSession, -1);
        var alone = resumed && !OwnerAlive(); resumed = false;
        if (!alone && (loggedOff < 0 || loggedOff != Volatile.Read(ref ownerSession))) return;
        ZapretRunner.Stop();
        if (session.State is null or NetworkSessionState.Released) return;
        NetworkHelperHost.RecordStep(DateTime.UtcNow.ToString("HH:mm:ss.fff") + (alone ? " пробуждение без окна" : " выход из сеанса Windows") + ": сеть возвращена");
        try { session.Recover(); }
        catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException) { NetworkHelperHost.RecordFailure(ex); }
    }

    private static bool OwnerAlive()
    {
        if (ownerPid == 0) return false;
        try { using var p = Process.GetProcessById(ownerPid); return !p.HasExited && p.StartTime == ownerStart; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    // Папка журнала создаётся заново, если её успел создать кто-то без прав администратора:
    // иначе пользователь мог бы подложить журнал, по которому служба восстанавливает сеть.
    private static string SecureDataRoot()
    {
        var root = DataRoot;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (Directory.Exists(root))
        {
            var owner = new DirectoryInfo(root).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (!Equals(owner, admins) && !Equals(owner, system)) Directory.Delete(root, recursive: true);
        }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(admins);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists) directory.Create(security); else directory.SetAccessControl(security);
        // Закрытая папка помощника: без права чтения для пользователей (NetworkHelperHost.PrivateFolder).
        var hidden = new DirectorySecurity();
        hidden.SetAccessRuleProtection(true, false);
        hidden.SetOwner(admins);
        hidden.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        hidden.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        var closed = new DirectoryInfo(Path.Combine(root, NetworkHelperHost.PrivateFolder));
        if (!closed.Exists) closed.Create(hidden); else closed.SetAccessControl(hidden);
        // До 0.18 правила и трассировка лежали прямо в папке, читаемой всеми.
        foreach (var old in new[] { "programs", "routing-lists" }.Select(n => Path.Combine(root, n)).Where(Directory.Exists)) Directory.Delete(old, recursive: true);
        foreach (var old in new[] { "direct-dns.json", "helper-trace.txt", "helper-failure.txt" }.Select(n => Path.Combine(root, n))) File.Delete(old);
        return root;
    }

    private static int OnControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is 1 or 5) // SERVICE_CONTROL_STOP, SERVICE_CONTROL_SHUTDOWN
        {
            Report(StateStopPending, 0);
            Stopping.Cancel();
        }
        // SERVICE_CONTROL_SESSIONCHANGE + WTS_SESSION_LOGOFF; в WTSSESSION_NOTIFICATION номер сеанса — после cbSize.
        else if (control == 0xE && eventType == 6 && eventData != IntPtr.Zero)
        {
            var id = Marshal.ReadInt32(eventData, 4);
            if (id != Volatile.Read(ref ownerSession)) return 0;
            Volatile.Write(ref loggedOffSession, id);
            lock (WakeGate) wake.Cancel();
        }
        // SERVICE_CONTROL_POWEREVENT + PBT_APMRESUMEAUTOMATIC.
        else if (control == 0xD && eventType == 0x12)
        {
            resumed = true;
            lock (WakeGate) wake.Cancel();
        }
        return 0;
    }

    private static void Report(int state, int exitCode)
    {
        var status = new Status
        {
            Type = 0x10, // SERVICE_WIN32_OWN_PROCESS
            State = state,
            Accepted = state == StateRunning ? 0xC5 : 0, // STOP | SHUTDOWN | POWEREVENT | SESSIONCHANGE
            ExitCode = exitCode,
            // Снятие сессии ждёт исчезновения адаптера до 25 с.
            WaitHint = state is StatePending or StateStopPending ? 60000 : 0
        };
        SetServiceStatus(statusHandle, ref status);
    }

    private delegate void ServiceMainProc(int argc, IntPtr argv);
    private delegate int HandlerProc(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TableEntry { public string? Name; public ServiceMainProc? Main; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Status { public int Type, State, Accepted, ExitCode, SpecificExitCode, CheckPoint, WaitHint; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcher(TableEntry[] table);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerProc handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
}

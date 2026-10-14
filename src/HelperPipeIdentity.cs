using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using System.Security.AccessControl;

namespace Oblachka;

public static class HelperPipeIdentity
{
    public static NamedPipeServerStream CreateServer(string name)
    {
        ValidateName(name);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UserError("Не удалось определить владельца helper.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false); security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, security);
    }
    public static string NewPipeName() => "Oblachka-" + Guid.NewGuid().ToString("N");
    public static void ValidateName(string name)
    {
        if (!name.StartsWith("Oblachka-", StringComparison.Ordinal) || name.Length != 41 ||
            !Guid.TryParseExact(name[9..], "N", out _)) throw new UserError("Неверное имя канала helper.");
    }

    public static void Client(NamedPipeServerStream pipe, Process owner)
    {
        // Retain a process handle so a recycled PID does not become a replacement owner.
        _ = owner.SafeHandle;
        if (owner.HasExited || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != owner.Id)
            throw new UserError("Канал подключён другим процессом.");
    }

    public static void Server(NamedPipeClientStream pipe, Process helper)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)), identity.User))
            throw new UserError("Канал принадлежит другой учётной записи.");
        _ = helper.SafeHandle;
        if (helper.HasExited || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != helper.Id)
            throw new UserError("Канал не принадлежит запущенному helper.");
    }

    // Канал службы. Имя постоянное, поэтому оно ничего не доказывает: подлинность сторон
    // проверяется по процессам (ServiceClient, ServiceServer).
    public const string ServicePipeName = "Oblachka-service";

    // Подключаться могут пользователи, вошедшие в систему на этом компьютере; по сети — нет.
    public static NamedPipeServerStream CreateServiceServer()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.SetOwner(system);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(ServicePipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 32768, 32768, security);
    }

    // Служба подчиняется только клиенту из своей же папки. Папка службы — в Program Files,
    // писать туда может только администратор, поэтому подменить клиента без прав нельзя.
    // Процесс клиента удерживается: повторно выданный PID не станет владельцем.
    public static Process ServiceClient(NamedPipeServerStream pipe, string serviceDirectory)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new UserError("Клиент канала не определён.");
        var client = Process.GetProcessById(pid);
        try
        {
            _ = client.SafeHandle;
            if (client.HasExited || !IsTrustedClient(ImagePath(client), serviceDirectory))
                throw new UserError("К службе подключилась посторонняя программа.");
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public static bool IsTrustedClient(string? image, string serviceDirectory)
    {
        if (string.IsNullOrEmpty(image)) return false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(image));
        var name = Path.GetFileName(image);
        return string.Equals(directory, Path.GetFullPath(serviceDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
               (name.Equals("Oblachka.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("Oblachka.Tests.exe", StringComparison.OrdinalIgnoreCase));
    }

    // Окно проверяет, что на том конце именно служба: PID сервера канала должен совпасть
    // с PID службы по данным диспетчера служб. Чужая программа, занявшая имя канала, не пройдёт.
    public static void ServiceServer(NamedPipeClientStream pipe, int servicePid)
    {
        if (servicePid <= 0 || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != servicePid)
            throw new UserError("Канал службы открыт не службой Облачки.");
    }

    // Путь исполняемого файла. Открывается с минимальным правом: так окно может узнать путь
    // и у процесса службы, работающей от SYSTEM.
    internal static string? ImagePath(Process process)
    {
        using var handle = OpenProcess(0x1000, false, process.Id); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) return null;
        var buffer = new System.Text.StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out int pid);
}

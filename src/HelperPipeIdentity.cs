using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using System.Security.AccessControl;

namespace RoutePilot;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out int pid);
}

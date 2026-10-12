using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Oblachka;

// Запуск ядра системного туннеля и владение его сроком жизни.
//
// Job Object здесь не роскошь: на стенде отдельно запущенное ядро пережило завершение
// запускавшего процесса и оставило поднятый интерфейс с маршрутами. Закрытие дескриптора
// задания завершает ядро вместе с владельцем, даже при аварийном завершении.
//
// Конфигурация передаётся через stdin и на диск не пишется: в ней адрес и параметры узла.
internal sealed class SingBoxTunnelProcess : ITunnelProcess
{
    private readonly Process process;
    private readonly ProcessJob job;
    private bool stopped;

    private SingBoxTunnelProcess(Process process, ProcessJob job)
    {
        this.process = process;
        this.job = job;
    }

    public bool IsRunning
    {
        get { try { return !process.HasExited; } catch (InvalidOperationException) { return false; } }
    }

    internal static ITunnelProcess Start(ValidatedNetworkLease lease, string executable, CancellationToken token)
    {
        var request = new NetworkLeaseRequest(1, lease.LeaseId, NetworkLeaseAction.Acquire, lease.LoopbackPort,
            lease.Peers.Select(p => p.ToString()).ToArray());
        var adapter = new CoreAdapter(CoreKind.SingBox, executable);
        // Конфигурацию проверяет само ядро до запуска: отказ виден до изменения сети.
        CoreConfigurationCheck.ValidateTunAsync(request, token).GetAwaiter().GetResult();
        var config = SingBoxTunConfig.Create(request).ToJsonString();

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in adapter.RunArguments) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys
            .Where(k => k.StartsWith("SING_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);

        var process = new Process { StartInfo = start };
        var job = new ProcessJob();
        try
        {
            process.Start();
            job.Attach(process);
            // Потоки ядра вычитываются, иначе процесс может заблокироваться на записи.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Write(config);
            process.StandardInput.Close();
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            job.Dispose();
            process.Dispose();
            throw;
        }
        return new SingBoxTunnelProcess(process, job);
    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { process.WaitForExit(5000); } catch (InvalidOperationException) { }
        job.Dispose();   // Закрытие задания добивает всё, что ядро могло породить.
        process.Dispose();
    }
}

public static class TunnelInterfaces
{
    // Присутствие интерфейса проверяется по списку сетевых интерфейсов Windows,
    // а не по имени процесса: адаптер может пережить ядро и наоборот.
    public static bool Exists(string name)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (NetworkInformationException) { return false; }
    }

    public static IReadOnlyList<string> Ours()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.Name.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Name).ToArray();
        }
        catch (NetworkInformationException) { return Array.Empty<string>(); }
    }
}

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
    internal static readonly TimeSpan GracefulStop = TimeSpan.FromSeconds(5);

    private SingBoxTunnelProcess(Process process, ProcessJob job)
    {
        this.process = process;
        this.job = job;
    }

    public bool IsRunning
    {
        get { try { return !process.HasExited; } catch (InvalidOperationException) { return false; } }
    }

    // Обратно в запрос — со всеми полями аренды. Потерянный здесь CoreDirect (0.10, 885e71d)
    // оставлял туннель без правила для прямого трафика ядра: он возвращался ядру по кругу,
    // и сайты из списков «напрямую» не открывались.
    internal static NetworkLeaseRequest RequestFor(ValidatedNetworkLease lease) =>
        new(1, lease.LeaseId, NetworkLeaseAction.Acquire, lease.LoopbackPort, lease.Peers.Select(p => p.ToString()).ToArray(),
            lease.SiteRules.ToArray(), lease.ProgramRules.ToArray(), lease.CoreDirect, lease.DirectByDefault, lease.Stats);

    internal static ITunnelProcess Start(ValidatedNetworkLease lease, string executable, CancellationToken token)
    {
        var request = RequestFor(lease);
        var adapter = new CoreAdapter(CoreKind.SingBox, executable);
        // Конфигурацию проверяет само ядро до запуска: отказ виден до изменения сети.
        var ipv6 = PhysicalNetwork.HasGlobalIpv6();
        // DNS по пути трафика: прямые сайты — через резолвер физической сети (DirectDns).
        DirectDns.EnsureFile();
        var localDns = DirectDns.PhysicalResolver();
        // Правила программ — в файлах, которые окно меняет на ходу (ProgramRuleSets); при запуске — из аренды.
        ProgramRuleSets.Write(ProgramRuleSets.Folder, lease.ProgramRules);
        CoreConfigurationCheck.ValidateTunAsync(request, token, ipv6, localDns, DirectDns.RuleSetPath, ProgramRuleSets.Folder).GetAwaiter().GetResult();
        var config = SingBoxTunConfig.Create(request, ipv6, localDns, DirectDns.RuleSetPath, ProgramRuleSets.Folder).ToJsonString();
        NetworkHelperHost.RecordStep(DateTime.UtcNow.ToString("HH:mm:ss.fff") + " DNS прямых сайтов: " + (localDns is null ? "через туннель" : "сеть"));

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
            // Своя группа процессов: мягкую остановку получает только ядро (ConsoleInterrupt).
            CreateNewProcessGroup = true
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
        // Сначала ядро просят завершиться самому: тогда sing-box удаляет свой адаптер
        // wintun. Убитое ядро оставляло адаптер «Obl-…» висеть до десятка минут — на
        // настоящей машине за один прогон проверки их набралось десять. Принудительно —
        // только если ядро не ответило.
        if (!ConsoleInterrupt.Send(process, GracefulStop, step => NetworkHelperHost.RecordStep(DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + step)))
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { process.WaitForExit(5000); } catch (InvalidOperationException) { }
        job.Dispose();   // Закрытие задания добивает всё, что ядро могло породить.
        process.Dispose();
    }
}

public static class PhysicalNetwork
{
    // Есть ли у физической сети глобальный IPv6: адрес из 2000::/3 и шлюз на работающем
    // интерфейсе, не нашем туннеле. Локальные fd00::/8 и fe80:: в счёт не идут — с ними
    // выхода в интернет по IPv6 нет.
    public static bool HasGlobalIpv6()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                !n.Name.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.Ordinal) &&
                n.GetIPProperties() is var p &&
                p.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) &&
                p.UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && IsGlobal(a.Address)));
        }
        catch (NetworkInformationException) { return false; }
    }

    public static bool IsGlobal(System.Net.IPAddress address) => (address.GetAddressBytes()[0] & 0xE0) == 0x20;
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

    public static bool IsUp(string name)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase) && i.OperationalStatus == OperationalStatus.Up);
        }
        catch (NetworkInformationException) { return false; }
    }

    // Список .NET включает и записи об уже удалённых адаптерах: они остаются в Windows
    // навсегда в состоянии Down, в «Сетевых подключениях» их нет. На настоящей машине
    // такие записи приняли за десять неснятых адаптеров. Живой — только поднятый.
    public static IReadOnlyList<string> Ours(bool upOnly = false)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.Name.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(i => !upOnly || i.OperationalStatus == OperationalStatus.Up)
                .Select(i => i.Name).ToArray();
        }
        catch (NetworkInformationException) { return Array.Empty<string>(); }
    }

    public static string DescribeLeft()
    {
        var up = Ours(upOnly: true);
        return (up.Count == 0 ? "нет" : string.Join(',', up)) + "; записей об удалённых: " + (Ours().Count - up.Count);
    }
}

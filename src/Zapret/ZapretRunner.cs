using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace Oblachka;

// zapret в службе: один процесс winws, привязанный к заданию службы — остановка или падение
// службы его гасит. Окно говорит, какую стратегию запустить, какие адреса не трогать (узлы VPN)
// и какие сайты пользователя добавить; всё это проверяется здесь же.
public enum ZapretAction { Prepare, Start, Stop, Status }

public sealed record ZapretRequest(ZapretAction Action, string? Strategy = null, string[]? ExcludeAddresses = null, string[]? Domains = null)
{
    private static readonly Regex Domain = new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant);

    public void Validate()
    {
        if (!Enum.IsDefined(Action)) throw new UserError("zapret: неизвестная команда.");
        if (Action == ZapretAction.Start && (Strategy is null || Strategy.Length is 0 or > 40)) throw new UserError("zapret: не указана стратегия.");
        if (Action != ZapretAction.Start && (Strategy ?? (object?)ExcludeAddresses ?? Domains) is not null) throw new UserError("zapret: лишние поля команды.");
        if (ExcludeAddresses is { } ips && (ips.Length > 32 || ips.Any(ip => !IPAddress.TryParse(ip, out _)))) throw new UserError("zapret: неверные адреса исключений.");
        if (Domains is { } domains && (domains.Length > 200 || domains.Any(d => !Domain.IsMatch(d)))) throw new UserError("zapret: неверный список сайтов.");
    }
}

// Ready — набор скачан и проверен; Running — winws работает; Error — понятная причина отказа.
public sealed record ZapretStatus(bool Ready, bool Running, string? Tag, string? Strategy, string[] Strategies, string? Error = null);

public static class ZapretRunner
{
    private static readonly object Gate = new();
    private static Process? process;
    private static ProcessJob? job;
    private static string? running;
    private static IReadOnlyList<WfpReinjection.PortRange> captured = Array.Empty<WfpReinjection.PortRange>();

    // Порты, которые перехватывает работающий winws: под защитой «Весь компьютер» на них
    // нужны разрешения для повторно отправленных пакетов (WfpReinjection).
    public static IReadOnlyList<WfpReinjection.PortRange>? CapturedPorts { get { lock (Gate) return process is { HasExited: false } ? captured : null; } }
    private static ZapretManifest? manifest;

    public static async Task<ZapretStatus> HandleAsync(ZapretRequest request, CancellationToken token)
    {
        request.Validate();
        try
        {
            switch (request.Action)
            {
                case ZapretAction.Prepare:
                    Stop();
                    var fresh = await ZapretInstaller.ManifestAsync(token).ConfigureAwait(false);
                    await ZapretInstaller.InstallAsync(fresh, token).ConfigureAwait(false);
                    manifest = fresh;
                    break;
                case ZapretAction.Start: Start(request); break;
                case ZapretAction.Stop: Stop(); break;
            }
            return Status();
        }
        catch (UserError ex) { return Status() with { Error = ex.Message }; }
    }

    public static ZapretStatus Status()
    {
        lock (Gate)
        {
            // После перезапуска службы набор на диске остаётся: проверяем его по встроенному манифесту.
            var current = manifest ??= Installed();
            var alive = process is { HasExited: false };
            return new(current is not null && ZapretInstaller.Ready(current), alive, current?.Tag, alive ? running : null,
                current?.Strategies.Select(s => s.Name).ToArray() ?? Array.Empty<string>());
        }
    }

    private static ZapretManifest? Installed()
    {
        try { var embedded = ZapretManifest.Embedded(); return ZapretInstaller.Ready(embedded) ? embedded : null; }
        catch (UserError) { return null; }
    }

    private static void Start(ZapretRequest request)
    {
        var current = (manifest ??= Installed()) ?? throw new UserError("zapret: набор ещё не подготовлен.");
        var strategy = current.Strategies.FirstOrDefault(s => s.Name == request.Strategy) ?? throw new UserError("zapret: такой стратегии нет в наборе.");
        if (ZapretInstaller.Damaged(current) is { Length: > 0 })
            throw new UserError("Файлы zapret повреждены или удалены — чаще всего это делает антивирус. Добавьте папку " + ZapretInstaller.Folder(current) + " в исключения и подготовьте набор заново.");
        if (ForeignBypass() is { } foreign) throw new UserError("Уже работает другой обход блокировок (" + foreign + "). Остановите его — например, через «Помощь → Сеть Windows».");
        Stop();
        var folder = ZapretInstaller.Folder(current);
        var lists = Path.Combine(folder, "lists");
        File.WriteAllLines(Path.Combine(lists, "ipset-exclude-user.txt"), request.ExcludeAddresses ?? Array.Empty<string>());
        File.WriteAllLines(Path.Combine(lists, "list-general-user.txt"), request.Domains ?? Array.Empty<string>());
        var bin = Path.Combine(folder, "bin");
        var start = new ProcessStartInfo(Path.Combine(bin, "winws.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = bin };
        // Только физическая сетевая карта: ни наш туннель, ни туннели других VPN winws не трогает.
        // Под защитой «Весь компьютер» его пакетам нужны разрешения (WfpReinjection).
        if (PhysicalInterface() is { } index) start.ArgumentList.Add("--wf-iface=" + index);
        var args = ZapretArgs.Resolve(strategy, bin, lists);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        lock (Gate)
        {
            var started = Process.Start(start) ?? throw new UserError("zapret: winws не запустился.");
            var newJob = new ProcessJob();
            newJob.Attach(started);
            process = started; job = newJob; running = strategy.Name; captured = WfpReinjection.Ports(args);
        }
        // Драйвер WinDivert не загрузился, неверные аргументы — winws завершается сразу.
        if (process!.WaitForExit(1500)) { var code = process.ExitCode; Stop(); throw new UserError("zapret завершился при запуске (код " + code + "). Возможно, драйвер WinDivert заблокирован антивирусом."); }
    }

    // Номер интерфейса для WinDivert: адаптер с основным шлюзом IPv4, не туннель (наш Obl-…,
    // Wintun, TAP и WireGuard других VPN). null — не нашли, перехват без ограничения.
    internal static int? PhysicalInterface()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)))
            {
                if (nic.Name.StartsWith("Obl-", StringComparison.OrdinalIgnoreCase) ||
                    new[] { "wintun", "tap-", "wireguard", "sing-box", "tun" }.Any(t => nic.Description.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                var properties = nic.GetIPProperties();
                if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                return properties.GetIPv4Properties()?.Index;
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return null;
    }

    public static void Stop()
    {
        lock (Gate)
        {
            job?.Dispose(); job = null;
            if (process is { } p) { try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(3000); } } catch (InvalidOperationException) { } p.Dispose(); }
            process = null; running = null;
        }
    }

    // Наш winws — только из папки набора в ProgramData (путь читается и у процесса SYSTEM).
    public static bool IsOurs(Process process) =>
        HelperPipeIdentity.ImagePath(process) is { } path && path.StartsWith(ZapretInstaller.Root + "\\", StringComparison.OrdinalIgnoreCase);

    // Чужой winws или GoodbyeDPI: два обхода на одном трафике ломают друг друга.
    internal static string? ForeignBypass()
    {
        var ours = ZapretInstaller.Root;
        foreach (var name in new[] { "winws", "goodbyedpi" })
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                {
                    if (!IsOurs(p)) return name == "winws" ? "zapret" : "GoodbyeDPI";
                }
        return null;
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Oblachka;

// Исправление сети с правами службы: четыре действия, без произвольных команд. Служба не верит
// окну на слово: каждую поломку проверяет заново и действует, только если она есть.
public sealed record RepairRequest([property: JsonRequired] RepairKind Kind, string? Target)
{
    public void Validate()
    {
        var ok = Kind switch
        {
            RepairKind.WinHttpProxy => Target is null,
            RepairKind.AdapterDns => Target is { Length: 38 } && Guid.TryParseExact(Target, "B", out _),
            RepairKind.DeadRoute => Target is { Length: <= 80 } && Target.Split('|') is { Length: 4 } p &&
                IPAddress.TryParse(p[0], out _) && IPAddress.TryParse(p[1], out _) && IPAddress.TryParse(p[2], out _) && int.TryParse(p[3], out _),
            RepairKind.StopBypass => Target is "zapret (winws)" or "GoodbyeDPI",
            _ => false
        };
        if (!ok) throw new UserError("Неизвестное исправление сети.");
    }
}

public static class NetworkRepairBackend
{
    // true — исправлено; false — поломки уже нет (нечего делать).
    public static bool Apply(RepairRequest request, INetworkProbe? probe = null)
    {
        request.Validate();
        probe ??= new WindowsNetworkProbe();
        switch (request.Kind)
        {
            case RepairKind.WinHttpProxy:
                if (!NetworkDoctor.LoopbackPorts(probe.WinHttpProxy()).Any(p => !probe.Listening(p))) return false;
                var info = new ProxyInfo { AccessType = 1 };
                if (!WinHttpSetDefaultProxyConfiguration(ref info)) throw new UserError("Windows не дала сбросить прокси служб.");
                return true;
            case RepairKind.AdapterDns:
                var adapter = probe.Adapters().FirstOrDefault(a => string.Equals(a.Id, request.Target, StringComparison.OrdinalIgnoreCase));
                var fake = adapter?.Dns.Where(NetworkDoctor.FakeDns).ToArray() ?? Array.Empty<IPAddress>();
                if (adapter is null || !adapter.StaticDns || fake.Length == 0 || fake.Any(probe.AnswersDns)) return false;
                var index = NetworkInterface.GetAllNetworkInterfaces().First(n => n.Id == adapter.Id).GetIPProperties().GetIPv4Properties().Index;
                Run("netsh.exe", "interface", "ipv4", "set", "dnsservers", "name=" + index, "source=dhcp");
                return true;
            case RepairKind.DeadRoute:
                var parts = request.Target!.Split('|');
                var route = probe.DefaultRoutes().FirstOrDefault(r => r.Destination == parts[0] && r.Mask == parts[1] && r.NextHop == parts[2] && r.InterfaceIndex.ToString() == parts[3]);
                if (route is null || route.InterfaceUp) return false;
                Run("route.exe", "delete", route.Destination, "mask", route.Mask, route.NextHop, "if", route.InterfaceIndex.ToString());
                return true;
            case RepairKind.StopBypass:
                var stopped = false;
                foreach (var name in WindowsNetworkProbe.BypassServices)
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
                    if (key is null) continue;
                    // Остановить и выключить автозапуск: включить обратно можно в «Службах» Windows.
                    Run("sc.exe", "stop", name); Run("sc.exe", "config", name, "start=", "demand");
                    stopped = true;
                }
                foreach (var name in request.Target == "GoodbyeDPI" ? new[] { "goodbyedpi" } : new[] { "winws" })
                    foreach (var process in Process.GetProcessesByName(name))
                        using (process) { try { process.Kill(); stopped = true; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
                return stopped;
            default: throw new UserError("Неизвестное исправление сети.");
        }
    }

    // Только системные программы из папки Windows и только список аргументов, без оболочки.
    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, program)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new UserError("Не удалось запустить " + program + ".");
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000)) { process.Kill(); throw new UserError(program + " не ответил."); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ProxyInfo { public uint AccessType; public IntPtr Proxy; public IntPtr Bypass; }
    [DllImport("winhttp.dll", SetLastError = true)] private static extern bool WinHttpSetDefaultProxyConfiguration(ref ProxyInfo info);
}

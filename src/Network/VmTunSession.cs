using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Oblachka;

// Owns both processes. Closing either the window or its parent ends the experiment.
internal sealed class VmTunSession : IDisposable
{
    private Engine? core;
    private Process? tun;
    private ProcessJob? job;
    internal string InterfaceName { get; private set; } = "";
    internal bool IsAlive => core?.ProcessId is not null && tun is { HasExited: false };
    internal bool InterfaceExists => NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == InterfaceName);

    internal async Task StartAsync(VmTunTicket ticket, Action<string> status, CancellationToken token)
    {
        if (!VmTunTickets.IsVirtualMachine() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UserError("Проверка доступна только внутри виртуалки с правами администратора.");
        var executable = Path.Combine(AppContext.BaseDirectory, "tools", ticket.Profile.Core == CoreKind.Xray ? "xray.exe" : "sing-box.exe");
        await CheckHashAsync(executable, ticket.Profile.Core == CoreKind.Xray ?
            "0D0FC0EA2B05641ACB78C01FC36AD694E7B029861B2D5EB93DA0E3E9FDA9A98F" :
            "5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF", token);
        await CheckHashAsync(Path.Combine(AppContext.BaseDirectory, "tools", "libcronet.dll"),
            "3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928", token);
        status("Проверяем выбранный сервер…");
        core = new Engine(executable, AppContext.BaseDirectory);
        await core.StartAsync(ticket.Profile, token);
        var probe = await core.ProbeAsync(new Uri("https://www.gstatic.com/generate_204"), token);
        if (!probe.Success) throw new UserError("Выбранный сервер не прошёл проверку SOCKS. Остановите проверку и выберите другой сервер в приложении.");
        var lease = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, core.Port, ticket.Peers);
        status("Проверяем конфигурацию TUN…");
        await CoreConfigurationCheck.ValidateTunAsync(lease, token);
        var config = SingBoxTunConfig.Create(lease);
        InterfaceName = config["inbounds"]![0]!["interface_name"]!.GetValue<string>();
        status("Создаём сетевой адаптер внутри виртуалки…");
        var start = new ProcessStartInfo(TunCore.Program())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("SING_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        foreach (var argument in new[] { "run", "-c", "stdin" }) start.ArgumentList.Add(argument);
        tun = new Process { StartInfo = start }; job = new ProcessJob();
        tun.Start(); job.Attach(tun);
        _ = DrainAsync(tun.StandardOutput); _ = DrainAsync(tun.StandardError);
        await tun.StandardInput.WriteAsync(config.ToJsonString().AsMemory(), token); tun.StandardInput.Close();
        for (var attempt = 0; attempt < 50 && !InterfaceExists; attempt++)
        {
            if (!IsAlive) throw new UserError("Ядро не смогло создать TUN. Проверка остановлена; возможно, недоступен драйвер адаптера.");
            await Task.Delay(100, token);
        }
        if (!InterfaceExists) throw new UserError("Адаптер TUN не появился за отведённое время.");
        status("Адаптер создан. Проверяем HTTPS через обычную сеть виртуалки…");
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var adapter = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Name == InterfaceName);
        var before = adapter.GetIPStatistics();
        foreach (var url in new[] { "https://www.gstatic.com/generate_204", "https://cp.cloudflare.com/generate_204" })
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode != 204) throw new UserError("TUN создан, но HTTPS-проверка не прошла.");
        }
        if (!IsAlive || !InterfaceExists) throw new UserError("Соединение прервалось во время проверки.");
        var after = adapter.GetIPStatistics();
        if (after.BytesSent <= before.BytesSent || after.BytesReceived <= before.BytesReceived)
            throw new UserError("HTTPS прошёл, но трафик на адаптере TUN не подтверждён. Успешной проверку не считаем.");
        status("TUN запущен: два HTTPS-запроса без настройки прокси прошли. Можно открыть браузер в виртуалке. DNS/IPv6-утечки и аварийное восстановление ещё не проверены.");
    }

    private static async Task CheckHashAsync(string path, string expected, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != expected)
            throw new UserError("Файлы ядра изменены. Распакуй сборку заново.");
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { var buffer = new char[2048]; while (await reader.ReadAsync(buffer.AsMemory()) != 0) { } }
        catch (IOException) { } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        job?.Dispose(); job = null;
        try { if (tun is not null && !tun.HasExited) { tun.Kill(); tun.WaitForExit(3000); } }
        catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        finally { tun?.Dispose(); tun = null; core?.Dispose(); core = null; }
    }
}

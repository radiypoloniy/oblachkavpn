using System.Diagnostics;

namespace Oblachka;

// Правила сайтов и программ на живом системном режиме. Отдельная сессия с правилами:
// ipify.org — напрямую, example.com — заблокирован, curl.exe — напрямую целиком.
// Ожидается: ipify показывает исходный выход машины, ifconfig.me — выход узла,
// example.com недоступен, curl к ifconfig.me — исходный выход.
internal static class SystemTunnelRules
{
    internal static void Run(TunnelEndpointPlan plan, Engine engine, Action<string> add, Func<string, string> probe)
    {
        HelperProcessLifetime? helper = null;
        SystemTunnelSession? session = null;
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            helper = HelperProcessLifetime.ConnectAsync(NetworkHelperLauncher.Launch, token.Token).GetAwaiter().GetResult();
            session = new SystemTunnelSession(engine, helper.Client);
            session.StartWithSparesAsync(new[] { plan }, token.Token,
                [RoutingPlan.Create("ipify.org", SiteAction.Direct), RoutingPlan.Create("example.com", SiteAction.Block)],
                [ProgramRoutingPlan.Create("curl.exe", SiteAction.Direct)]).GetAwaiter().GetResult();
            add("правила: сессия " + session.State + ", блокировка " + (session.BlocksBypass ? "стоит" : "НЕ стоит") + ", " + WfpStateProbe.Describe().Split(';')[0]);
            add("правила: ipify.org (напрямую): " + probe("https://api.ipify.org"));
            add("правила: ifconfig.me (через туннель): " + probe("https://ifconfig.me/ip"));
            add("правила: example.com (блок): " + probe("https://example.com/"));
            add("правила: curl.exe к ifconfig.me (программа напрямую): " + Curl("https://ifconfig.me/ip"));
            add("правила: curl.exe без проверки отзыва: " + Curl("https://ifconfig.me/ip", "--ssl-no-revoke"));
            add("правила: curl.exe по HTTP: " + Curl("http://ifconfig.me/ip"));
            add("правила: curl.exe -4: " + Curl("https://ifconfig.me/ip", "-4"));
            add("правила: curl.exe -6: " + Curl("https://ifconfig.me/ip", "-6"));
            session.StopAsync().GetAwaiter().GetResult();
            add("правила: после остановки " + session.State + ", " + WfpStateProbe.Describe().Split(';')[0]);
        }
        catch (Exception ex) { add("правила: сценарий прерван (" + ex.GetType().Name + (ex is UserError ? ": " + ex.Message : "") + ")"); }
        finally
        {
            try { session?.StopAsync().GetAwaiter().GetResult(); } catch (Exception) { }
            try { helper?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception) { }
        }
    }

    private static string Curl(string url, string? extra = null)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "curl.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-sS", "-m", "20", url }) info.ArgumentList.Add(a);
        if (extra is not null) info.ArgumentList.Add(extra);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd().Trim(); var error = p.StandardError.ReadToEnd().Trim(); p.WaitForExit(25000);
        return p.ExitCode == 0 ? output : "ошибка curl " + p.ExitCode + ": " + (error.Length > 160 ? error[..160] : error);
    }
}

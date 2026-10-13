using System.Net;

namespace Oblachka;

// Правила сайтов в обычном режиме: их исполняет само ядро подключения, сеть машины не
// меняется — только локальный SOCKS.
//   Oblachka.Tests.exe --local-rules-check <ключ> <имя узла> <отчёт>
// ipify.org — напрямую, example.com — блок. Через SOCKS: ipify показывает выход машины,
// ifconfig.me — выход узла, example.com недоступен.
internal static class LocalRulesCheckCommand
{
    internal static int Run(string key, string nodeName, string reportPath)
    {
        var log = new List<string>();
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var imported = Subscriptions.FetchAsync(ServiceKey.ToSubscriptionUrl(key), token.Token).GetAwaiter().GetResult();
            var profile = imported.Profiles.First(p => p.DisplayName.Contains(nodeName, StringComparison.OrdinalIgnoreCase));
            log.Add("узел: " + profile.DisplayName + " · " + profile.Core);
            log.Add("выход машины без прокси: " + Get(null, "https://api.ipify.org"));
            using var engine = new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "")
            {
                SiteRules = [RoutingPlan.Create("ipify.org", SiteAction.Direct), RoutingPlan.Create("example.com", SiteAction.Block)]
            };
            engine.StartAsync(profile, token.Token).GetAwaiter().GetResult();
            log.Add("через SOCKS, ipify.org (напрямую): " + Get(engine.Port, "https://api.ipify.org"));
            log.Add("через SOCKS, ifconfig.me (через узел): " + Get(engine.Port, "https://ifconfig.me/ip"));
            log.Add("через SOCKS, example.com (блок): " + Get(engine.Port, "https://example.com/"));
            engine.Stop();
            File.WriteAllLines(reportPath, log);
            return 0;
        }
        catch (Exception ex) { log.Add("отказ: " + ex.GetType().Name + " " + (ex is UserError ? ex.Message : "")); File.WriteAllLines(reportPath, log); return 2; }
    }

    private static string Get(int? port, string url)
    {
        try
        {
            using var handler = new SocketsHttpHandler { UseProxy = port is not null, Proxy = port is null ? null : new WebProxy("socks5://127.0.0.1:" + port, false) };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            return client.GetStringAsync(url).GetAwaiter().GetResult().Trim() is var text && text.Length > 40 ? "ответ " + text.Length + " байт" : text;
        }
        catch (Exception ex) { return "ошибка (" + ex.GetType().Name + ")"; }
    }
}

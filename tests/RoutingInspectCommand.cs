using System.Diagnostics;
using System.Text.Json;

namespace Oblachka;

// Только настройки маршрутизации указанного сайта: без подписок, ключей и адресов серверов.
internal static class RoutingInspectCommand
{
    internal static int Run(string host)
    {
        var snapshot = new ClientStateStore(ClientDataPaths.StateFile).Load();
        if (snapshot is null) return 1;
        var browsers = new List<object>();
        foreach (var process in Process.GetProcesses().Where(p => p.ProcessName is "chrome" or "msedge" or "firefox" or "browser"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is not { } path) continue;
                    browsers.Add(new { Browser = process.ProcessName, Decision = RouteDecision.Of(snapshot.ProgramRules, snapshot.RouteRules, path, host)?.ToString() });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            snapshot.SystemTunnel, snapshot.SystemProxy, snapshot.RussiaDirect,
            DirectDns = snapshot.Advanced?.DirectDns,
            SiteMatches = snapshot.RouteRules.Where(r => RouteDecision.Covers(r.Domain, host)).Select(r => new { r.Domain, Action = r.Action.ToString() }),
            Browsers = browsers.DistinctBy(b => JsonSerializer.Serialize(b))
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}

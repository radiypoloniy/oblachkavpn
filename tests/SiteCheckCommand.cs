namespace Oblachka;

// Живая проверка «Не открывается сайт?» по прямому пути (из этого процесса), без ядер и без VPN-сессии:
//   Oblachka.Tests.exe --site-check <файл отчёта> [socks:<порт>] <сайт> [<сайт>…]
internal static class SiteCheckCommand
{
    internal static int Run(string reportPath, IEnumerable<string> sites)
    {
        var lines = new List<string>();
        SocksRoute? via = null;
        foreach (var site in sites)
        {
            if (site.StartsWith("socks:", StringComparison.Ordinal)) { via = new SocksRoute(int.Parse(site[6..])); continue; }
            var host = SiteProbe.NormalizeHost(site);
            if (host is null) { lines.Add(site + ": не адрес сайта"); continue; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var path = SiteProbe.CheckAsync(host, via, timeout.Token).GetAwaiter().GetResult();
            lines.Add($"{host}: {path.Stage} {path.Status} {path.Milliseconds} мс — {SiteVerdict.Short(path)}");
        }
        File.WriteAllLines(reportPath, lines);
        return 0;
    }
}

namespace Oblachka;

// Живая проверка отклика серверов: тот же ServerPing, что у окна при запуске.
//   Oblachka.Tests.exe --ping-check <ключ или ссылка> <файл отчёта>
// В отчёт попадают имена узлов и отклик; ключ и адреса узлов — нет.
internal static class PingCheckCommand
{
    internal static int Run(string key, string reportPath)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var profiles = Subscriptions.FetchAsync(ServiceKey.ToSubscriptionUrl(key), timeout.Token).GetAwaiter().GetResult().Profiles;
            var lines = new List<string>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ServerPing.PingAllAsync(profiles, (p, ms) => { lock (lines) lines.Add(p.DisplayName + ": " + (ms is null ? "нет ответа" : ms + " мс")); }, timeout.Token)
                .GetAwaiter().GetResult();
            lines.Add($"Узлов {profiles.Count}, откликов {lines.Count}, за {clock.ElapsedMilliseconds} мс.");
            File.WriteAllLines(reportPath, lines);
            return 0;
        }
        catch (UserError ex) { File.WriteAllText(reportPath, ex.Message); return 1; }
    }
}

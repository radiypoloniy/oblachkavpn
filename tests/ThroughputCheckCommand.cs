namespace Oblachka;

// Живая проверка замера скорости автовыбора (ServerMeasurements.ThroughputAsync) через SOCKS ядра:
//   Oblachka.Tests.exe --throughput-check <порт SOCKS> <файл отчёта>
// Три замера подряд: скорость и сколько заняло.
internal static class ThroughputCheckCommand
{
    internal static int Run(int socksPort, string reportPath)
    {
        var lines = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var speed = ServerMeasurements.ThroughputAsync(socksPort, CancellationToken.None).GetAwaiter().GetResult();
            lines.Add($"замер {i + 1}: {speed / 1_000_000.0:0.00} МБ/с за {clock.ElapsedMilliseconds} мс");
        }
        File.WriteAllLines(reportPath, lines);
        return 0;
    }
}

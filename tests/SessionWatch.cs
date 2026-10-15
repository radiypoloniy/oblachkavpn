using System.Diagnostics;

namespace Oblachka;

// Долгое удержание системного режима с замерами и проверка IPv6.
//
// Удержание делится на шаги по 15 с: на каждом — запрос выходного адреса через систему
// (то есть через туннель). В отчёт идёт строка на шаг и сводка: доля успешных, медиана
// времени и самый длинный провал. Провал во время обрыва линка ожидаем; важно, что
// связь возвращается сама, а мимо туннеля в это время ничего не уходит.
internal static class SessionWatch
{
    internal static void Hold(int seconds, Action<string> add, Func<string, string> probe)
    {
        var total = Math.Clamp(seconds, 1, 3600);
        var start = Stopwatch.StartNew();
        var results = new List<(double At, bool Ok, long Ms)>();
        while (start.Elapsed.TotalSeconds < total)
        {
            var watch = Stopwatch.StartNew();
            var answer = probe("https://api.ipify.org");
            var ok = !answer.StartsWith("ошибка", StringComparison.Ordinal);
            results.Add((start.Elapsed.TotalSeconds, ok, watch.ElapsedMilliseconds));
            if (total > 120) add($"  +{start.Elapsed.TotalSeconds:0} с: {(ok ? answer + ", " + watch.ElapsedMilliseconds + " мс" : answer)}");
            var rest = TimeSpan.FromSeconds(15) - watch.Elapsed;
            if (rest > TimeSpan.Zero && start.Elapsed.TotalSeconds < total) Thread.Sleep(rest);
        }
        if (total <= 120) return;
        var good = results.Where(r => r.Ok).Select(r => r.Ms).OrderBy(x => x).ToArray();
        double gap = 0, failedSince = -1;
        foreach (var r in results)
        {
            if (!r.Ok && failedSince < 0) failedSince = r.At;
            if (r.Ok && failedSince >= 0) { gap = Math.Max(gap, r.At - failedSince); failedSince = -1; }
        }
        if (failedSince >= 0) gap = Math.Max(gap, start.Elapsed.TotalSeconds - failedSince);
        add($"удержание {total} с: успешных {good.Length} из {results.Count}, медиана {(good.Length > 0 ? good[good.Length / 2] : 0)} мс, " +
            $"самый длинный провал {gap:0} с" + (failedSince >= 0 ? ", связь к концу не вернулась" : ""));
    }

    // IPv6-только адрес: до туннеля показывает собственный IPv6 машины, если он есть.
    // При поднятом туннеле этот адрес появиться не должен.
    internal static string Ipv6(Func<string, string> probe) => probe("https://api6.ipify.org");
}

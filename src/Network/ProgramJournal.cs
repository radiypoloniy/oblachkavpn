namespace Oblachka;

// Какие программы выходили в сеть за последние 10 минут — для «Сообщить о проблеме». Снимок
// «Сейчас в сети» видит только открытые сейчас соединения: программа, что стучится на сервер
// раз в минуту на пару секунд (античит, лаунчер), в него почти не попадает. А именно она бывает
// причиной, когда игра идёт напрямую, а её спутник — через VPN (AION2, 24.10.2026). Окно раз в
// 5 с читает соединения ядра туннеля (CoreConnections) и запоминает по программе: когда её
// видели, сколько раз и каким путём. Сайты не запоминаются — только пути программ.
public sealed class ProgramJournal
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public const int MaxPrograms = 200;

    public sealed record Entry(string Path, DateTime FirstSeen, DateTime LastSeen, int Samples, int Vpn, int Direct, int Udp);

    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    // Vpn, Direct, Udp — наибольшее число соединений за один замер.
    public void Record(IEnumerable<CoreConnection> connections, DateTime now)
    {
        var programs = connections.Where(c => c.ProcessPath is { Length: > 0 }).GroupBy(c => c.ProcessPath!, StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            foreach (var program in programs)
            {
                int vpn = program.Count(c => !c.Direct), direct = program.Count(c => c.Direct), udp = program.Count(c => c.Udp);
                entries[program.Key] = entries.TryGetValue(program.Key, out var seen)
                    ? seen with { LastSeen = now, Samples = seen.Samples + 1, Vpn = Math.Max(seen.Vpn, vpn), Direct = Math.Max(seen.Direct, direct), Udp = Math.Max(seen.Udp, udp) }
                    : new(program.Key, now, now, 1, vpn, direct, udp);
            }
            Trim(now);
        }
    }

    public IReadOnlyList<Entry> Recent(DateTime now)
    {
        lock (gate) { Trim(now); return entries.Values.OrderByDescending(e => e.LastSeen).ToArray(); }
    }

    private void Trim(DateTime now)
    {
        foreach (var old in entries.Values.Where(e => now - e.LastSeen > Window).Select(e => e.Path).ToArray()) entries.Remove(old);
        if (entries.Count > MaxPrograms)
            foreach (var extra in entries.Values.OrderBy(e => e.LastSeen).Take(entries.Count - MaxPrograms).Select(e => e.Path).ToArray()) entries.Remove(extra);
    }
}

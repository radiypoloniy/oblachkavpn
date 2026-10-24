namespace Oblachka;

// Какие сайты zapret идут напрямую. Группы решаются отдельно: стратегия может открыть
// YouTube, но не Discord (его в РФ режут ещё и по адресам). Раньше засчитывалась только
// стратегия, открывшая всё, и YouTube оставался в VPN, хотя напрямую работал. Теперь группа
// идёт напрямую, если открылись все её проверочные адреса, иначе остаётся в VPN.
public sealed record ZapretGroup(string Key, string Title, string List, int[] Targets);

public static class ZapretPlan
{
    public const string NoBypass = "-";

    // Адреса — номера в ZapretProbe.Targets; списки доменов — файлы набора Flowseal.
    public static readonly ZapretGroup[] Groups =
    [
        new("youtube", "YouTube", "list-google.txt", [0, 1, 2]),
        new("discord", "Discord и сайты из общего списка", "list-general.txt", [3, 4, 5])
    ];

    public static string[] Passing(ZapretProbeResult probe) =>
        Groups.Where(g => g.Targets.All(i => i < probe.Ok.Count && probe.Ok[i])).Select(g => g.Key).ToArray();

    // Выбор запоминается строкой «стратегия|группы». Запись до 0.13.3 — только имя стратегии
    // (её засчитывали, лишь когда открывалось всё), поэтому она значит «все группы».
    public static string Encode(string strategy, IEnumerable<string> groups) => strategy + "|" + string.Join(',', groups);

    public static (string Strategy, string[] Groups) Decode(string choice)
    {
        var bar = choice.IndexOf('|');
        if (bar < 0) return (choice, Groups.Select(g => g.Key).ToArray());
        var keys = choice[(bar + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(k => Groups.Any(g => g.Key == k)).ToArray();
        return (choice[..bar], keys);
    }

    // Сшивка: YouTube открыла одна стратегия, Discord — другая → «A + B» (ZapretStrategy.Combine).
    // first — первая стратегия, открывшая каждую группу.
    public static string? Stitch(IReadOnlyDictionary<string, string> first) =>
        first.TryGetValue("youtube", out var youtube) && first.TryGetValue("discord", out var rest) && youtube != rest ? youtube + ZapretStrategy.Joiner + rest : null;

    public static string StitchTitle(string strategy) =>
        strategy.Split(ZapretStrategy.Joiner) is [var youtube, var rest] ? $"YouTube — из «{youtube}», Discord — из «{rest}»" : $"«{strategy}»";

    // Какая стратегия обходит группу: у сшитой YouTube — из первой части, остальное — из второй.
    public static string StrategyFor(string strategy, string group) =>
        strategy.Split(ZapretStrategy.Joiner) is [var youtube, var rest] ? (group == "youtube" ? youtube : rest) : strategy;

    // Лучший вариант — тот, при котором напрямую открывается больше групп; при равенстве —
    // без обхода (winws не нужен), затем более ранний в списке.
    public static bool Better(string[] candidate, string[] current) => candidate.Length > current.Length;

    // Подпись в настройках: что идёт напрямую, что через VPN и какие адреса не открылись.
    // vpn — группы, которые человек сам оставил в VPN.
    public static string Describe(string strategy, string[] direct, ZapretProbeResult? lastProbe, IReadOnlyCollection<string>? vpn = null)
    {
        var parts = new List<string>();
        foreach (var group in Groups)
        {
            if (vpn?.Contains(group.Key) == true) parts.Add(group.Title + " — через VPN, так выбрано.");
            else if (direct.Contains(group.Key))
            {
                var quic = lastProbe is null ? [] : group.Targets.Where(i => lastProbe.QuicOnly.Contains(i)).Select(i => ZapretProbe.Targets[i].Host).ToArray();
                parts.Add(group.Title + " — напрямую" + (strategy == NoBypass ? ", обход не нужен" : $" через zapret («{StrategyFor(strategy, group.Key)}»)") +
                    (quic.Length > 0 ? " (" + string.Join(", ", quic) + " — по QUIC, как в браузере)." : "."));
            }
            else
            {
                var failed = lastProbe is null ? Array.Empty<string>()
                    : group.Targets.Where(i => i < lastProbe.Ok.Count && !lastProbe.Ok[i]).Select(i => ZapretProbe.Targets[i].Host).ToArray();
                parts.Add(group.Title + " — через VPN" + (failed.Length > 0 ? ": не открылись " + string.Join(", ", failed) + "." : "."));
            }
        }
        return string.Join(" ", parts);
    }
}

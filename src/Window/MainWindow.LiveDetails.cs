using Oblachka.Views;

namespace Oblachka;

public partial class MainWindow
{
    private sealed record LiveObservation(DateTime ReceivedAt, IReadOnlyList<CoreConnection>? Core);
    private LiveObservation? liveObservation;

    private void RefreshLiveDetails()
    {
        if (liveRows.Values.FirstOrDefault(row => row.Expanded) is not { } row) return;
        if (liveObservation is not { } observation)
        {
            row.Observation = "Ждём свежую выборку соединений.";
            foreach (var site in row.Sites) site.Evidence = "";
            return;
        }

        var source = observation.Core is null ? "Таблицы Windows" : "Ядро туннеля";
        row.Observation = $"{source} · получено в {observation.ReceivedAt:T} · {row.ObservedRoute}";

        RouteDecision? policy = null;
        foreach (var site in row.Sites.Where(s => s.Expanded))
        {
            if (observation.Core is null || site.Ids.Length == 0) { site.Evidence = ""; continue; }
            policy ??= sentRules.Compile();
            site.Evidence = ExplainSiteSample(observation.Core, site.Ids, policy, tunnelDirectAllowed);
        }
    }

    internal static string ExplainSiteSample(IReadOnlyList<CoreConnection> connections, string[] ids, RouteDecision policy, bool directAllowed)
    {
        // Строка сайта объединяет файлы и поддомены. Правило проверяем для исходного соединения.
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var lines = connections.Where(c => c.Id is { } id && selected.Contains(id)).Take(3).Select(c =>
        {
            var destination = c.Host ?? c.Address ?? "Адрес неизвестен";
            if (c.Port > 0) destination += " · порт " + c.Port;
            var endpoint = $"{Path.GetFileName(c.ProcessPath)} → {destination} · {(c.Udp ? "UDP" : "TCP")}";
            var observed = c.Direct ? "прямой выход" : c.Chains.Contains("local-core") ? "передано ядру прокси"
                : c.Chains.Contains(SingBoxTunConfig.ServerSelector) ? "выход через VPN" : "выход неизвестен";
            var match = policy.Find(c);
            var calculated = match is { } rule ? DescribeRule(rule) : "совпадений среди правил программ и сайтов нет";
            if (match?.Action == SiteAction.Direct && !directAllowed) calculated += " · прямой выход требует переподключения";
            return $"{endpoint}\nЯдро наблюдало — {observed}\nПо текущим отправленным правилам — {calculated}";
        }).ToArray();
        if (lines.Length == 0) return "";
        var more = ids.Length > lines.Length ? $"\n\nПоказано {lines.Length} из {ids.Length} соединений" : "";
        return string.Join("\n\n", lines) + more;
    }

    private static string DescribeRule(RouteRuleMatch rule)
    {
        var action = RuleRow.ActionLabel(rule.Action);
        if (rule.Program is { } program)
        {
            var target = program.AllInFolder ? "папка " + Path.GetFileName(program.Folder) : program.Executable;
            return program.Domain is { } domain ? $"{action} · {target} и {domain}" : $"{action} · {target}";
        }
        return $"{action} · {rule.Site!.Domain} для всех программ";
    }
}

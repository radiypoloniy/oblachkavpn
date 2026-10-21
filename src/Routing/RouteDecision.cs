namespace Oblachka;

// Какое правило ядро туннеля применит к соединению программы с сайтом — тем же порядком, что в
// конфигурации: правило «программа и сайт», правило программы, правило сайта для всех программ.
// null — правила нет, соединение идёт общим путём. По этому окно решает, какие открытые соединения
// закрыть после смены правил: только те, чей путь действительно поменялся.
public static class RouteDecision
{
    public static SiteAction? Of(IReadOnlyList<ProgramRule> programs, IReadOnlyList<SiteRule> sites, string? path, string? host)
    {
        if (path is not null)
        {
            if (host is not null && ProgramRoutingPlan.Ordered(programs).FirstOrDefault(r => r.Domain is { } domain && Covers(domain, host) && ProgramRoutingPlan.Covers(r, path)) is { } both)
                return both.Action;
            if (ProgramRoutingPlan.Find(programs, path) is { } program) return program.Action;
        }
        return host is null ? null : sites.Where(s => Covers(s.Domain, host)).OrderByDescending(s => s.Domain.Length).FirstOrDefault()?.Action;
    }

    public static bool Covers(string domain, string host) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}

// Правила, отправленные ядру туннеля: программы (с правилами «программа и сайт») и сайты.
public sealed record SentRules(IReadOnlyList<ProgramRule> Programs, IReadOnlyList<SiteRule> Sites)
{
    public static SentRules None { get; } = new([], []);
    public string Key => string.Join('|', Programs.Select(r => r.Key + ">" + r.Action)) + "#" + string.Join('|', Sites.Select(s => s.Domain + ">" + s.Action));
    public SiteAction? Of(string? path, string? host) => RouteDecision.Of(Programs, Sites, path, host);
}

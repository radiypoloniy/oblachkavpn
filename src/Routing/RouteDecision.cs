using System.Net;

namespace Oblachka;

internal readonly record struct RouteRuleMatch(SiteAction Action, ProgramRule? Program = null, SiteRule? Site = null);

// Расчёт по отправленным правилам программ и сайтов в порядке файлов живого туннеля.
// null — среди этих правил нет совпадения. Системные правила, списки и выход по умолчанию здесь не оцениваются.
public sealed class RouteDecision
{
    private readonly ProgramRule[] programs;
    private readonly (SiteRule Rule, Func<string?, IPAddress?, int, bool> Matches)[] sites;

    internal RouteDecision(IReadOnlyList<ProgramRule> programs, IReadOnlyList<SiteRule> sites)
    {
        this.programs = ProgramRuleSets.InRouteOrder(programs).ToArray();
        this.sites = SiteRuleSets.InRouteOrder(sites).Select(r => (r, MatchSite(r.Domain))).ToArray();
    }

    public static SiteAction? Of(IReadOnlyList<ProgramRule> programs, IReadOnlyList<SiteRule> sites, string? path, string? host) =>
        new RouteDecision(programs, sites).Find(path, host, null, 0)?.Action;

    internal SiteAction? Of(CoreConnection connection) => Find(connection)?.Action;

    internal RouteRuleMatch? Find(CoreConnection connection) => Find(connection.ProcessPath, connection.Host, connection.Address, connection.Port);

    private RouteRuleMatch? Find(string? path, string? host, string? address, int port)
    {
        if (path is not null)
            foreach (var program in programs)
                if ((program.Domain is null || host is not null && Covers(program.Domain, host)) && ProgramRoutingPlan.Covers(program, path))
                    return new(program.Action, Program: program);

        IPAddress.TryParse(address, out var ip);
        foreach (var site in sites)
            if (site.Matches(host, ip, port)) return new(site.Rule.Action, Site: site.Rule);
        return null;
    }

    private static Func<string?, IPAddress?, int, bool> MatchSite(string target)
    {
        switch (SiteTarget.Kind(target))
        {
            case SiteTargetKind.Subnet:
                var network = IPNetwork.Parse(target);
                return (_, ip, _) => ip is not null && ip.AddressFamily == network.BaseAddress.AddressFamily && network.Contains(ip);
            case SiteTargetKind.Port:
                var (low, high) = SiteTarget.Port(target);
                return (_, _, port) => port >= low && port <= high;
            default:
                return (host, _, _) => host is not null && Covers(target, host);
        }
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
    internal RouteDecision Compile() => new(Programs, Sites);
}

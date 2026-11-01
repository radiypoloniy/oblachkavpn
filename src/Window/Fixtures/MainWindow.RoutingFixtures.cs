namespace Oblachka;

public partial class MainWindow
{
    private void CheckDirectDnsRules(List<string> checks)
    {
        if (sentDirectDns is not null) throw new InvalidOperationException("First session can inherit a stale DNS rule file");
        var sites = new[] { RoutingPlan.Create("goldapple.ru", SiteAction.Direct), RoutingPlan.Create("blocked.invalid", SiteAction.Block) };
        var direct = DirectDomains(null, sites);
        if (!direct.SequenceEqual(new[] { "goldapple.ru" }) || !RouteDecision.Covers("goldapple.ru", "www.goldapple.ru") ||
            RouteDecision.Covers("goldapple.ru", "goldapple.ru.evil.invalid"))
            throw new InvalidOperationException("Site exception has incorrect DNS scope");
        var routing = new CompiledRouting(new(), new(), new(), true, ["direct", "proxy", "block"], []);
        for (var i = 0; i < DirectDnsRequest.MaxParts * DirectDnsRequest.MaxPerPart; i++) routing.Direct.Suffix.Add($"site{i}.invalid");
        if (DirectDomains(routing, sites)[0] != "goldapple.ru") throw new InvalidOperationException("Provider lists displaced explicit DNS exception");
        checks.Add("Live site DNS uses explicit exceptions first, covers subdomains and clears previous-session state");
    }
}

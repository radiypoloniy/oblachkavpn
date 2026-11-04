using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SingBoxTunConfig
{
    // Explicit user rules come first. The service routes precede imported
    // lists and the default. A pinned exit is never the global selector.
    private static void AddServiceRoutes(JsonObject config, IReadOnlyList<PinnedServiceRoute> services)
    {
        var rules = config["route"]!["rules"]!.AsArray();
        static JsonObject Pin(JsonObject match, int? exit)
        {
            match["inbound"] = new JsonArray("system-tun");
            return exit is int index ? Route(match, SiteAction.Tunnel, DirectTag(index)) : Route(match, SiteAction.Block, ServerSelector);
        }
        // An exact executable can claim all its connections. For traffic
        // from shared hosts (browsers, IDEs) the most specific domain wins.
        foreach (var service in services)
            foreach (var file in service.Files)
                rules.Add(Pin(ProgramRoutingPlan.Match(file), service.ExitIndex));
        foreach (var target in services.SelectMany(s => s.Domains.Select(d => (Domain: d, s.ExitIndex)))
                     .OrderByDescending(d => SiteTarget.Order(d.Domain)))
            rules.Add(Pin(SiteTarget.SingBox(target.Domain), target.ExitIndex));
    }
}

using System.Text.Json.Nodes;

namespace Oblachka;

// Saved intent uses a profile identity. An index is meaningful only in one lease.
public sealed record ServiceRoute(string Id, string ProfileId, string[] Domains, ProgramRule[] Files);
public sealed record PinnedServiceRoute(string Id, int? ExitIndex, string[] Domains, ProgramRule[] Files);

internal static class ServiceRoutePlan
{
    internal const int MaxRoutes = 8, MaxTargets = 64;

    internal static void Validate(IReadOnlyList<ServiceRoute> routes)
    {
        if (routes.Count > MaxRoutes || routes.Any(r => r is null || !ClientServerCatalog.ValidId(r.ProfileId)))
            throw new UserError("Неверные профили маршрутов сервисов.");
        Validate(routes.Select(r => new PinnedServiceRoute(r.Id, null, r.Domains, r.Files)).ToArray(), null);
    }

    internal static void Validate(IReadOnlyList<PinnedServiceRoute> routes, IReadOnlyList<JsonObject?>? exits)
    {
        if (routes.Count > MaxRoutes) throw new UserError("Слишком много маршрутов сервисов.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var domains = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = 0;
        foreach (var route in routes)
        {
            if (route is null || route.Id is not { Length: > 0 and <= 32 } ||
                route.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) || !ids.Add(route.Id) ||
                route.Domains is null || route.Files is null || route.Domains.Length > MaxTargets || route.Files.Length > MaxTargets ||
                (targets += route.Domains.Length + route.Files.Length) > MaxTargets || route.Domains.Length + route.Files.Length == 0)
                throw new UserError("Неверный состав маршрутов сервисов.");
            if (route.ExitIndex is int index && (index < 0 || exits is null || index >= exits.Count || exits[index] is null))
                throw new UserError("Маршрут сервиса ссылается на выход вне этой сессии.");
            foreach (var domain in route.Domains)
                if (string.IsNullOrWhiteSpace(domain) || SiteTarget.Kind(domain) != SiteTargetKind.Domain ||
                    RoutingPlan.Create(domain, SiteAction.Tunnel).Domain != domain || !domains.Add(domain))
                    throw new UserError("Для маршрута сервиса нужен уникальный домен.");
            ProgramRoutingPlan.Validate(route.Files);
            if (route.Files.Any(f => f.Folder is null || f.AllInFolder || f.Domain is not null ||
                f.Action != SiteAction.Tunnel || !files.Add(f.TargetKey)))
                throw new UserError("Для маршрута сервиса нужен уникальный точный путь к файлу.");
        }
    }

    internal static PinnedServiceRoute[] Bind(IReadOnlyList<ServiceRoute> routes, IReadOnlyList<TunnelEndpointPlan> plans,
        IReadOnlyList<JsonObject?>? exits, Func<Profile, bool>? allowed)
    {
        Validate(routes);
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; exits is not null && i < exits.Count && i < plans.Count; i++)
        {
            var profile = plans[i].Source;
            if (exits[i] is not null && !profile.IsFree && allowed?.Invoke(profile) != false)
                indices.TryAdd(ClientServerCatalog.Id(profile)!, i);
        }
        return routes.Select(r => new PinnedServiceRoute(r.Id, indices.TryGetValue(r.ProfileId, out var index) ? index : null,
            r.Domains.ToArray(), r.Files.ToArray())).ToArray();
    }
}

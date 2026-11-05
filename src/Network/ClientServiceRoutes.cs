namespace Oblachka;

internal sealed record ServicePreset(string Id, string Name, string[] Domains);

internal sealed partial class ClientServiceRoutes(ClientRuntime owner)
{
    internal static IReadOnlyList<ServicePreset> Presets { get; } = Array.AsReadOnly<ServicePreset>(
    [
        new("claude", "Claude и Claude Code", ["claude.ai", "claude.com", "api.anthropic.com", "console.anthropic.com"]),
        new("openai", "ChatGPT и Codex", ["chatgpt.com", "auth.openai.com", "chat.openai.com", "api.openai.com", "oaistatic.com", "oaiusercontent.com"]),
        new("discord", "Discord", ["discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com"]),
        new("github", "GitHub", ["github.com", "githubusercontent.com", "githubassets.com", "ghcr.io"]),
        new("steam", "Steam", ["steampowered.com", "steamcommunity.com", "steamstatic.com", "steamcontent.com"]),
        new("spotify", "Spotify", ["spotify.com", "scdn.co", "spotifycdn.com"]),
        new("telegram", "Telegram", ["telegram.org", "t.me", "telegra.ph"]),
        new("youtube", "YouTube", ["youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "youtube-nocookie.com"])
    ]);

    internal static string Title(string id) => Presets.FirstOrDefault(p => p.Id == id)?.Name ?? id;
    internal static string Title(ServiceRoute route) => route.Id.StartsWith("custom-", StringComparison.Ordinal)
        ? string.Join(", ", route.Domains.Concat(route.Files.Select(f => f.Executable))) : Title(route.Id);

    internal bool HasPendingChanges => owner.Tunnel is { State: SystemTunnelState.Running } &&
        owner.Tunnels.Applied is { } applied &&
        (applied.ServiceRoutes.Length != owner.TunnelPolicy.ServiceRoutes.Count ||
         owner.TunnelPolicy.ServiceRoutes.Any(route => !MatchesApplied(route)));

    private bool MatchesApplied(ServiceRoute route) =>
        owner.Tunnels.Applied?.ServiceRoutes.FirstOrDefault(r => r.Id == route.Id) is { } active &&
        string.Equals(active.ProfileId, route.ProfileId, StringComparison.OrdinalIgnoreCase) &&
        active.Domains.SequenceEqual(route.Domains) && active.Files.SequenceEqual(route.Files);

    internal ServiceRoute Draft(ServicePreset preset, Profile profile, ProgramRule[] files)
    {
        if (!owner.Profiles.Imported.Contains(profile) || !owner.Tunnels.ServiceProfileAllowed(profile))
            throw new UserError("Этот сервер недоступен в текущем наборе правил.");
        return new(preset.Id, ClientServerCatalog.Id(profile)!, preset.Domains.ToArray(), files.ToArray());
    }

    internal string[] Conflicts(ServiceRoute route)
    {
        var policy = owner.TunnelPolicy;
        var programs = policy.LeasePrograms().Where(rule =>
            (rule.Domain is null || route.Domains.Any(domain => Overlaps(domain, rule.Domain))) ||
            route.Files.Any(file => rule.AllInFolder
                ? file.FilePath!.StartsWith(rule.Folder + "\\", StringComparison.OrdinalIgnoreCase)
                : string.Equals(file.Executable, rule.Executable, StringComparison.OrdinalIgnoreCase) &&
                  (rule.Folder is null || string.Equals(rule.Folder, file.Folder, StringComparison.OrdinalIgnoreCase))));
        return policy.LeaseSites(owner.Profiles.Selected?.IsFree == true)
            .Where(rule => route.Files.Length > 0 || SiteTarget.Kind(rule.Domain) != SiteTargetKind.Domain ||
                           route.Domains.Any(domain => Overlaps(domain, rule.Domain)))
            .Select(rule => rule.Domain + " · " + rule.Label)
            .Concat(programs.Select(rule => rule.ToString())).Distinct().ToArray();
    }

    private static bool Overlaps(string left, string right) =>
        left == right || left.EndsWith("." + right, StringComparison.Ordinal) || right.EndsWith("." + left, StringComparison.Ordinal);

    internal void Save(ServiceRoute route)
    {
        var routes = owner.TunnelPolicy.ServiceRoutes;
        var index = routes.FindIndex(r => r.Id == route.Id);
        ServiceRoutePlan.Validate(routes.Where(r => r.Id != route.Id).Append(route).ToArray());
        if (index < 0) routes.Add(route); else routes[index] = route;
        owner.Persistence.Queue();
    }

    internal void Remove(string id)
    {
        if (owner.TunnelPolicy.ServiceRoutes.RemoveAll(r => r.Id == id) > 0) owner.Persistence.Queue();
    }
}

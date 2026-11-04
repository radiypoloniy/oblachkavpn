namespace Oblachka;

public sealed record ClientTunnelSettings(SiteRule[] Sites, ProgramRule[] Programs, RoutingProfile[] Routing,
    TunnelTuning? Tuning, bool DirectByDefault, bool LocalNetwork, bool QuicOverTcp, bool CompatibleStack,
    bool ForeignNode, bool Fragment, ProviderFragment? FragmentShape, bool Zapret)
{
    public ServiceRoute[] ServiceRoutes { get; init; } = [];
}

// Источник текущей конфигурации; снимок строится непосредственно перед запуском новой сессии.
public sealed class ClientTunnelPolicy
{
    private readonly ClientRuntime owner;
    public ClientTunnelPolicy(ClientRuntime owner) { this.owner = owner; }
    public List<SiteRule> Sites { get; } = new();
    public List<ProgramRule> Programs { get; } = new();
    public List<ServiceRoute> ServiceRoutes { get; } = new();
    public AdvancedOptions Advanced { get; set; } = AdvancedOptions.Default;
    public bool Selective { get; set; }
    public bool Zapret { get; set; }
    public RoutingProfile[] ZapretProfiles { get; set; } = [];
    public RoutingProfile? ProviderRouting { get; set; }
    public bool? UseProviderRouting { get; set; }
    public ProviderOptions? Provider => owner.Profiles.Groups.FirstOrDefault(g => g.Id == ProfileGroup.ServiceId)?.Metadata?.Provider;
    public static readonly string[] SelectiveDomains = ["youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com", "youtube-nocookie.com", "youtubei.googleapis.com",
        "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com", "discord.dev", "discordstatus.com"];
    public static readonly string[] SelectivePrograms = ["Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe"];

    public SiteRule[] LeaseSites(bool foreign)
    {
        var sites = !Selective ? Sites.ToArray() : Sites.Concat(SelectiveDomains.Where(d => !Sites.Any(r => r.Domain == d))
            .Select(d => RoutingPlan.Create(d, SiteAction.Tunnel))).Take(100).ToArray();
        return foreign ? ForeignGuard.With(sites, 100) : sites;
    }
    public ProgramRule[] LeasePrograms()
    {
        var rules = !Selective ? Programs.ToArray() : Programs.Concat(SelectivePrograms
            .Where(p => !Programs.Any(r => string.Equals(r.Executable, p, StringComparison.OrdinalIgnoreCase)))
            .Select(p => ProgramRoutingPlan.Create(p, SiteAction.Tunnel))).Take(100).ToArray();
        return rules.Concat((Provider?.DirectApps ?? []).Where(a => !rules.Any(r => string.Equals(r.Executable, a, StringComparison.OrdinalIgnoreCase)))
            .Select(a => ProgramRoutingPlan.Create(a, SiteAction.Direct))).Take(100).ToArray();
    }
    public IEnumerable<RoutingProfile> RoutingProfiles()
    {
        foreach (var profile in ZapretProfiles) yield return profile;
        if (Selective && !owner.Preferences.SystemTunnel)
            yield return new RoutingProfile("Через VPN только YouTube и Discord", [], SelectiveDomains.Select(d => "domain:" + d).ToArray(), [], [], [], []) { GlobalProxy = false };
        if (RoutingProfile.Custom(Advanced.GeoDirect, Advanced.GeoProxy, Advanced.GeoBlock) is { } custom) yield return custom;
        if (UseProviderRouting == true && ProviderRouting is { } provider) yield return provider;
        if (owner.Preferences.RussiaDirect) yield return RoutingProfile.RussiaDirect;
    }
    public ClientTunnelSettings Snapshot(Profile selected)
    {
        var preferences = owner.Preferences; var data = owner.Profiles;
        var fragment = data.FragmentMode == FragmentMode.Always || data.FragmentMode == FragmentMode.Auto && (data.FragmentLearned || Provider?.Fragment is { Enabled: true });
        return new(LeaseSites(selected.IsFree), LeasePrograms(), RoutingProfiles().ToArray(),
            TunnelTuning.From(Advanced, Provider, preferences.CompatibleStack), Selective, preferences.LocalNetwork,
            preferences.QuicOverTcp, preferences.CompatibleStack, selected.IsFree, fragment,
            Advanced.Fragment(Provider?.Fragment is { Enabled: true } shape ? shape : null), Zapret)
        { ServiceRoutes = ServiceRoutes.ToArray() };
    }
    public static string[] DirectDomains(CompiledRouting? routing, IEnumerable<SiteRule> rules)
    {
        if (routing is { GlobalProxy: false }) return [];
        return rules.Where(r => r.Action == SiteAction.Direct && SiteTarget.Kind(r.Domain) == SiteTargetKind.Domain).Select(r => r.Domain)
            .Concat(routing?.Direct.Suffix ?? []).Concat(routing?.Direct.Full ?? [])
            .Select(n => n.Trim().TrimStart('.').ToLowerInvariant()).Where(DirectDnsRequest.Valid).Distinct(StringComparer.Ordinal)
            .Take(DirectDnsRequest.MaxParts * DirectDnsRequest.MaxPerPart).ToArray();
    }
}

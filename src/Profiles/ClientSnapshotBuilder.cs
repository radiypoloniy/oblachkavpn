namespace Oblachka;

public static class ClientSnapshotBuilder
{
    public static ClientSnapshot Capture(ClientRuntime owner)
    {
        var profiles = owner.Profiles; var data = owner.Workspace; var policy = owner.TunnelPolicy; var preferences = owner.Preferences;
        var selected = profiles.Selected;
        var savedGroups = profiles.Groups.Where(g => g.Source != ProfileSource.Free).ToArray();
        var service = profiles.Groups.FirstOrDefault(g => g.Id == ProfileGroup.ServiceId);
        var serviceSources = service?.Sources ?? [];
        var preferred = service?.PreferredSource ?? 0;
        var externalSources = profiles.Groups.FirstOrDefault(g => g.Source == ProfileSource.External && g.IsSubscription)?.Sources ?? [];
        var persistedSet = data.PersistedSet(owner);
        IReadOnlyList<Profile> ProfilesOf(string id) => profiles.GroupProfiles.TryGetValue(id, out var list) ? list : [];
        return new(1,
        // Все серверы групп, со скрытыми: иначе после перезапуска их нечем было бы вернуть.
        // Бесплатные чужие узлы (FreeServers) не сохраняются: после перезапуска их нет.
        savedGroups.SelectMany(g => ProfilesOf(g.Id)).Select(p => new SavedProfile(p.Name, (System.Text.Json.Nodes.JsonObject)p.Outbound.DeepClone()) { Source = p.Source, Group = p.Group }).ToArray(),
        selected is null || selected.IsFree ? null : Profiles.Fingerprint(selected), serviceSources.ToArray(), preferred,
        preferences.StayInTray, preferences.AutoRecover)
        { SelectedGroup = selected is { IsFree: false } ? selected.Group : null, SelectedName = selected is { IsFree: false } ? selected.Name : null, ExternalSources = externalSources.ToArray(), AutoRefresh = preferences.AutoRefresh, SystemProxy = preferences.SystemProxy, CloseToTray = preferences.StayInTray, SystemTunnel = preferences.SystemTunnel, Theme = ThemeManager.Choice, Metadata = service?.Metadata, Groups = savedGroups.ToArray(), RouteRules = persistedSet.Sites, ProgramRules = persistedSet.Programs,
            Favorites = data.Favorites.Where(f => profiles.Imported.Any(p => AutoPool.Key(p) == f)).Take(Subscriptions.MaxProfiles).ToArray(),
            HiddenServers = data.HiddenServers.Take(Subscriptions.MaxProfiles).ToArray(), RemovedSubscriptions = data.RemovedSubscriptions.ToArray(),
            Measurements = profiles.Measurements.Values.Where(m => profiles.Imported.Any(p => !p.IsFree && Profiles.Fingerprint(p) == m.Fingerprint)).Take(200).ToArray(),
            ProviderRouting = policy.ProviderRouting, UseProviderRouting = policy.UseProviderRouting, RussiaDirect = preferences.RussiaDirect, AutoSelect = profiles.AutoSelect,
            UpdateCheck = preferences.UpdateAuto, UpdatePrerelease = preferences.UpdatePrerelease, DpiBypass = profiles.FragmentMode,
            ZapretEnabled = policy.Zapret, SelectiveVpn = policy.Selective, ZapretChoices = new Dictionary<string, string>(data.ZapretChoices), ZapretVpnGroups = data.ZapretVpnGroups.Order(StringComparer.Ordinal).ToArray(),
            AutoPool = profiles.AutoPool.Snapshot(profiles.Imported),
            ConnectOnLaunch = preferences.ConnectOnLaunch, WasConnected = data.WasConnected, Notices = preferences.Notices,
            KnownNetworks = data.KnownNetworks.Count == 0 ? null : new(data.KnownNetworks), SecureUnknown = preferences.SecureUnknown, Lockdown = preferences.Lockdown,
            MachinePick = profiles.MachineMode, MachineMemory = data.MachineMemory.Count == 0 ? null : data.MachineMemory.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value)),
            ServiceHosts = ServiceBrand.LearnedIn(profiles.Groups.SelectMany(g => g.Sources).Concat(serviceSources)), LocalNetwork = preferences.LocalNetwork,
            QuicOverTcp = preferences.QuicOverTcp, CompatibleStack = preferences.CompatibleStack, Advanced = policy.Advanced.IsDefault ? null : policy.Advanced, ServerTweaks = profiles.Tweaks.Count == 0 ? null : new(profiles.Tweaks),
            RuleSets = data.RuleSets.Count == 0 ? null : data.SavedRuleSets(owner).Where(s => s.Name != persistedSet.Name).ToArray(), RuleSetName = data.RuleSets.Count == 0 && persistedSet.Name == ClientWorkspace.MainRuleSet ? null : persistedSet.Name,
            RuleSetServers = persistedSet.Servers, TrafficDay = data.TrafficDay, TrafficBytes = data.TrafficToday, TrafficHours = data.TrafficHours.ToArray(), TrafficHistory = data.TrafficHistory.Snapshot(DateTime.Now),
            ServiceRoutes = policy.ServiceRoutes.Count == 0 ? null : policy.ServiceRoutes.ToArray(),
            Hotkey = preferences.Hotkey, FreeConsent = data.FreeConsent, FreeList = data.FreeList.ToString() };
    }
}

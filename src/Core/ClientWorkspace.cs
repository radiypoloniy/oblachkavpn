namespace Oblachka;

// Сохраняемые пользовательские данные без ссылок на представление.
public sealed class ClientWorkspace
{
    public const string MainRuleSet = "Основной";
    public HashSet<string> Favorites { get; } = new(StringComparer.Ordinal);
    public HashSet<string> HiddenServers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RemovedSubscriptions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnownNetwork> KnownNetworks { get; } = new();
    public Dictionary<string, Dictionary<string, string>> MachineMemory { get; } = new();
    public List<RuleSet> RuleSets { get; } = new();
    public string RuleSetName { get; set; } = MainRuleSet;
    public Dictionary<string, string> ZapretChoices { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ZapretVpnGroups { get; } = new(StringComparer.Ordinal);
    public string TrafficDay { get; set; } = "";
    public long TrafficToday { get; set; }
    public long[] TrafficHours { get; } = new long[24];
    public TrafficHistory TrafficHistory { get; } = new();
    public bool WasConnected { get; set; }
    public int FreeConsent { get; set; }
    public FreeList? FreeList { get; set; }

    public RuleSet PersistedSet(ClientRuntime owner) => new RuleSet(RuleSetName,
        owner.TunnelPolicy.Sites.ToArray(), owner.TunnelPolicy.Programs.ToArray(), owner.Profiles.RuleSetServers?.ToArray());
    public RuleSet[] SavedRuleSets(ClientRuntime owner) =>
        RuleSets.Where(s => s.Name != RuleSetName).Append(PersistedSet(owner))
            .OrderBy(s => s.Name == MainRuleSet ? 0 : 1).ToArray();
}

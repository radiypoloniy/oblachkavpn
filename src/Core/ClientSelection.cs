namespace Oblachka;

// Выбор использует данные приложения и не зависит от списка WPF.
public sealed class ClientSelection(ClientRuntime owner)
{
    public string? NetworkKey { get; set; }
    public Profile? BeforeConnect() => owner.Profiles.AutoSelect ? BestAuto() :
        owner.Profiles.MachineMode && owner.Profiles.Selected is { } selected ? BestMachine(
            owner.Profiles.Imported.Where(p => ServerMachines.Key(p) == ServerMachines.Key(selected)).ToArray() is { Length: > 0 } members ? members : [selected]) : owner.Profiles.Selected;

    public Profile? BestAuto()
    {
        var data = owner.Profiles;
        var all = data.Imported.ToArray();
        var members = data.AutoPool.Members(all);
        var pool = members.Where(p => data.RuleSetServers is null || !p.IsFree && data.RuleSetServers.Contains(ServerMachines.Hash(p))).ToArray();
        if (pool.Length == 0) pool = members;
        var measurements = all.Select(data.LatestMeasurement).OfType<ServerMeasurement>()
            .GroupBy(m => m.Fingerprint).ToDictionary(g => g.Key, g => g.First());
        return ServerMeasurements.Best(pool, measurements, DateTimeOffset.UtcNow, data.Selected) is int best ? pool[best] :
            data.Selected is { IsService: true } current && data.AutoPool.Contains(current) ? current :
            pool.FirstOrDefault(p => p.IsService) ?? pool.FirstOrDefault() ?? data.Selected;
    }
    public Profile BestMachine(IReadOnlyList<Profile> members)
    {
        var remembered = NetworkKey is { } key && owner.Workspace.MachineMemory.TryGetValue(key, out var known) &&
            known.TryGetValue(ServerMachines.Hash(members[0]), out var print) ? print : null;
        return ServerMachines.Best(members, owner.Profiles.LatestMeasurement, remembered, DateTimeOffset.UtcNow);
    }
}

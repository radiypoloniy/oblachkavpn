namespace Oblachka;

public sealed class ClientProfiles
{
    public ProfilePickerState Picker { get; } = new();
    public Profile? Selected { get; set; }
    public Profile? Running { get; set; }
    public bool AutoSelect { get; set; }
    public bool MachineMode { get; set; }
    public IReadOnlyList<Profile> Imported { get; set; } = Array.Empty<Profile>();
    public List<ProfileGroup> Groups { get; } = new();
    public Dictionary<string, IReadOnlyList<Profile>> GroupProfiles { get; } = new();
    public Dictionary<string, ServerTweak> Tweaks { get; } = new(StringComparer.Ordinal);
    public AutoPool AutoPool { get; } = new();
    public HashSet<string>? RuleSetServers { get; set; }
    public Dictionary<string, ServerMeasurement> Measurements { get; } = new();
    public Dictionary<string, ServerMeasurement> Pings { get; } = new();
    public FragmentMode FragmentMode { get; set; } = FragmentMode.Auto;
    public bool FragmentLearned { get; set; }
    public Diagnosis? LastDiagnosis { get; set; }
    public bool InAutoPool(Profile item) => AutoPool.Contains(item) &&
        (RuleSetServers is null || !item.IsFree && RuleSetServers.Contains(ServerMachines.Hash(item)));
    public ServerMeasurement? LatestMeasurement(Profile item)
    {
        var key = Profiles.Fingerprint(item); Measurements.TryGetValue(key, out var full);
        if (full is not null && ServerMeasurements.Fresh(full, DateTimeOffset.UtcNow)) return full;
        return Pings.TryGetValue(key, out var ping) ? ping : full;
    }
    public void MarkMeasured(Profile item, long? milliseconds)
    {
        var key = Profiles.Fingerprint(item);
        Pings[key] = new(key, DateTimeOffset.UtcNow, milliseconds is not null, Math.Max(1, milliseconds ?? 0));
    }
    public ServerTweak? TweakFor(Profile item) => Tweaks.GetValueOrDefault(ServerTweak.Key(item));

    public void Clear()
    {
        Picker.ItemsSource = Array.Empty<Profile>();
        Selected = Running = null; Imported = Array.Empty<Profile>();
        Groups.Clear(); GroupProfiles.Clear(); Tweaks.Clear(); Measurements.Clear(); Pings.Clear();
    }
}

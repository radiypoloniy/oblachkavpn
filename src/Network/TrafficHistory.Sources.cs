namespace Oblachka;

internal readonly record struct TrafficSource(string Id, string Name);

public sealed record TrafficSourceStats(string Id, string Name, long Upload, long Download)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Total => Upload + Download;
}

public sealed partial class TrafficHistory
{
    private const int MaxTrafficSources = 64;
    private const string OtherSource = "other";

    private sealed partial class DayState
    {
        public readonly Dictionary<string, TrafficSourceStats> Sources = new(StringComparer.Ordinal);
    }

    internal void AddSource(TrafficSource source, long upload, long download, DateTime now)
    {
        var counts = Day(now).Sources;
        if (!counts.ContainsKey(source.Id) && counts.Count >= MaxTrafficSources - 1)
            source = new(OtherSource, "Другие VPN-профили");
        counts.TryGetValue(source.Id, out var old);
        var name = new string(source.Name.Where(c => !char.IsControl(c)).Take(256).ToArray()).Trim();
        counts[source.Id] = new(source.Id, name.Length == 0 ? "VPN-профиль" : name, (old?.Upload ?? 0) + upload, (old?.Download ?? 0) + download);
    }

    private TrafficSourceStats[] ReportSources(DateTime now, int periodDays)
    {
        var sources = new Dictionary<string, TrafficSourceStats>(StringComparer.Ordinal);
        for (var offset = periodDays - 1; offset >= 0; offset--)
        {
            if (!days.TryGetValue(Key(now.AddDays(-offset)), out var day)) continue;
            foreach (var source in day.Sources.Values)
            {
                sources.TryGetValue(source.Id, out var old);
                sources[source.Id] = source with { Upload = source.Upload + (old?.Upload ?? 0), Download = source.Download + (old?.Download ?? 0) };
            }
        }
        return sources.Values.OrderByDescending(s => s.Total).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool ValidSources(TrafficSourceStats[]? sources) => sources is null || sources.Length <= MaxTrafficSources &&
        sources.All(s => s is not null && (s.Id == OtherSource || ClientServerCatalog.ValidId(s.Id)) &&
            s.Name is { Length: > 0 and <= 256 } && !s.Name.Any(char.IsControl) && s.Upload >= 0 && s.Download >= 0) &&
        sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == sources.Length;
}

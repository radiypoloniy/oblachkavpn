namespace Oblachka;

public readonly record struct TrafficRouteSample(long Vpn, long Direct, long Unknown)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Total => Vpn + Direct + Unknown;

    internal static TrafficRouteSample From(long total, TrafficRouteBytes routes) =>
        new(routes.Vpn, routes.Direct, Math.Max(0, total - routes.Vpn - routes.Direct));

    public static TrafficRouteSample operator +(TrafficRouteSample a, TrafficRouteSample b) =>
        new(a.Vpn + b.Vpn, a.Direct + b.Direct, a.Unknown + b.Unknown);
}

public sealed partial class TrafficHistory
{
    private sealed partial class DayState
    {
        public TrafficRouteSample[]? RouteHours;
    }

    private static bool ValidRouteHours(TrafficRouteSample[]? values) => values is null ||
        values.Length == 24 && values.All(v => v.Vpn >= 0 && v.Direct >= 0 && v.Unknown >= 0);

    private TrafficRouteSample[] ReportRoutePoints(DateTime now, int daysInPeriod)
    {
        if (daysInPeriod == 1)
            return days.GetValueOrDefault(Key(now))?.RouteHours?.ToArray() ?? new TrafficRouteSample[24];

        var points = new TrafficRouteSample[daysInPeriod];
        for (var i = 0; i < points.Length; i++)
        {
            if (!days.TryGetValue(Key(now.AddDays(i + 1 - daysInPeriod)), out var day)) continue;
            foreach (var value in day.Programs.Values)
                points[i] += TrafficRouteSample.From(value.Upload + value.Download, value.Routes);
        }
        return points;
    }
}

using System.Globalization;

namespace Oblachka;

public sealed record SubscriptionMetadata(long? Upload, long? Download, long? Total, DateTimeOffset? Expires)
{
    public static SubscriptionMetadata? Parse(IEnumerable<string> headers)
    {
        var text = string.Join(";", headers);
        if (text.Length > 2048) return null;
        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0 || !values.TryAdd(pair[0], n)) return null;
        }
        long? Get(string key) => values.TryGetValue(key, out var n) ? n : null;
        DateTimeOffset? expires = null;
        if (Get("expire") is > 0 and var timestamp)
        {
            try { expires = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return values.Count == 0 ? null : new(Get("upload"), Get("download"), Get("total"), expires);
    }
    public string Summary(DateTimeOffset now)
    {
        var parts = new List<string>();
        if (Expires is not null) parts.Add((Expires < now ? "Срок истёк " : "До ") + Expires.Value.ToLocalTime().ToString("dd.MM.yyyy"));
        if (Total is > 0 && Upload is >= 0 && Download is >= 0)
        {
            var remaining = Math.Max(0m, (decimal)Total.Value - Upload.Value - Download.Value);
            parts.Add($"Осталось {remaining / 1073741824m:0.##} ГБ");
        }
        return string.Join(" · ", parts);
    }
}

public sealed class SubscriptionSchedule
{
    private DateTimeOffset due = DateTimeOffset.MinValue;
    private int failures;
    public bool IsDue(DateTimeOffset now) => now >= due;
    public void Complete(DateTimeOffset now, bool success)
    {
        failures = success ? 0 : Math.Min(failures + 1, 5);
        due = now.Add(success ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, failures - 1))));
    }
    public void AccessDenied() => due = DateTimeOffset.MaxValue;
}

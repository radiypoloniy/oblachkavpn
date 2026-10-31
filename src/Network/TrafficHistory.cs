using System.Globalization;
using System.Text;

namespace Oblachka;

public sealed record TrafficProgramStats(string Path, long Upload, long Download);
public sealed record TrafficDayStats(string Day, long Upload, long Download, long[] UploadHours, long[] DownloadHours, TrafficProgramStats[] Programs);
public sealed record AnalyticsPoint(string Label, long Upload, long Download)
{
    public long Total => Upload + Download;
}
public sealed record AnalyticsProgram(string Path, long Upload, long Download)
{
    public long Total => Upload + Download;
}
public sealed record AnalyticsReport(AnalyticsPoint[] Points, AnalyticsProgram[] Programs, long Upload, long Download)
{
    public long Total => Upload + Download;
    public bool Empty => Total == 0 && Programs.Length == 0;
}

// История только локальная: даты, байты и пути программ. Адреса сайтов сюда не передаются.
public sealed class TrafficHistory
{
    public const int MaxDays = 30;
    public const int MaxPrograms = 256;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly Dictionary<string, DayState> days = new(StringComparer.Ordinal);

    private sealed class DayState
    {
        public long Upload, Download;
        public readonly long[] UploadHours = new long[24], DownloadHours = new long[24];
        public readonly Dictionary<string, (long Upload, long Download)> Programs = new(StringComparer.OrdinalIgnoreCase);

        public TrafficDayStats Snapshot(string day) => new(day, Upload, Download, UploadHours.ToArray(), DownloadHours.ToArray(),
            Programs.Select(p => new TrafficProgramStats(p.Key, p.Value.Upload, p.Value.Download))
                .OrderByDescending(p => p.Upload + p.Download).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Take(MaxPrograms).ToArray());
    }

    public void Restore(TrafficDayStats[]? saved, string? legacyDay, long legacyBytes, long[]? legacyHours, DateTime now)
    {
        days.Clear();
        foreach (var day in saved ?? [])
        {
            if (day.Programs is null || day.UploadHours is not { Length: 24 } || day.DownloadHours is not { Length: 24 }) continue;
            var state = new DayState { Upload = day.Upload, Download = day.Download };
            Array.Copy(day.UploadHours, state.UploadHours, 24); Array.Copy(day.DownloadHours, state.DownloadHours, 24);
            foreach (var program in day.Programs) state.Programs[program.Path] = (program.Upload, program.Download);
            days[day.Day] = state;
        }
        if (days.Count == 0 && legacyDay == Key(now) && legacyBytes > 0)
        {
            var state = Day(now);
            state.Download = legacyBytes;
            if (legacyHours is { Length: 24 }) Array.Copy(legacyHours, state.DownloadHours, 24);
        }
        Trim(now);
    }

    public TrafficDayStats[] Snapshot(DateTime now)
    {
        Trim(now);
        return days.OrderByDescending(p => p.Key, StringComparer.Ordinal).Select(p => p.Value.Snapshot(p.Key)).ToArray();
    }

    public TrafficDayStats Today(DateTime now) => days.TryGetValue(Key(now), out var state) ? state.Snapshot(Key(now)) : EmptyDay(now);

    public void Add(long upload, long download, DateTime now)
    {
        if (upload < 0 || download < 0) throw new ArgumentOutOfRangeException(nameof(upload));
        if (upload == 0 && download == 0) return;
        var state = Day(now);
        state.Upload += upload; state.Download += download;
        state.UploadHours[now.Hour] += upload; state.DownloadHours[now.Hour] += download;
    }

    internal void AddProgram(string path, long upload, long download, DateTime now)
    {
        var programs = Day(now).Programs;
        programs.TryGetValue(path, out var old);
        programs[path] = (old.Upload + upload, old.Download + download);
    }

    public void Reset() => days.Clear();

    public AnalyticsReport Report(DateTime now, int periodDays)
    {
        if (periodDays is not 1 and not 7 and not 30) throw new ArgumentOutOfRangeException(nameof(periodDays));
        Trim(now);
        var points = new List<AnalyticsPoint>();
        var programs = new Dictionary<string, (long Upload, long Download)>(StringComparer.OrdinalIgnoreCase);
        long upload = 0, download = 0;
        for (var offset = periodDays - 1; offset >= 0; offset--)
        {
            var date = now.Date.AddDays(-offset);
            days.TryGetValue(Key(date), out var state);
            if (periodDays == 1)
            {
                for (var hour = 0; hour < 24; hour++)
                    points.Add(new(hour.ToString("00", CultureInfo.InvariantCulture), state?.UploadHours[hour] ?? 0, state?.DownloadHours[hour] ?? 0));
            }
            else points.Add(new(date.ToString("dd.MM", Ru), state?.Upload ?? 0, state?.Download ?? 0));
            if (state is null) continue;
            upload += state.Upload; download += state.Download;
            foreach (var (path, value) in state.Programs)
            {
                programs.TryGetValue(path, out var old);
                programs[path] = (old.Upload + value.Upload, old.Download + value.Download);
            }
        }
        return new(points.ToArray(), programs.Select(p => new AnalyticsProgram(p.Key, p.Value.Upload, p.Value.Download))
            .OrderByDescending(p => p.Total).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToArray(), upload, download);
    }

    internal static bool Valid(TrafficDayStats[] history) => history.Length <= MaxDays &&
        history.All(d => d is not null && d.Upload >= 0 && d.Download >= 0 &&
            DateTime.TryParseExact(d.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
            d.UploadHours is { Length: 24 } && d.DownloadHours is { Length: 24 } && d.UploadHours.All(n => n >= 0) && d.DownloadHours.All(n => n >= 0) &&
            d.Programs is not null && d.Programs.Length <= MaxPrograms && d.Programs.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Path) &&
                p.Path.Length <= 1024 && !p.Path.Any(char.IsControl) && p.Upload >= 0 && p.Download >= 0)) &&
        history.Select(d => d.Day).Distinct(StringComparer.Ordinal).Count() == history.Length;

    public static string Csv(AnalyticsReport report)
    {
        static string Cell(string value)
        {
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var csv = new StringBuilder("Период;Отправлено байт;Получено байт;Всего байт\r\n");
        foreach (var point in report.Points)
            csv.Append(Cell(point.Label)).Append(';').Append(point.Upload).Append(';').Append(point.Download).Append(';').AppendLine(point.Total.ToString(CultureInfo.InvariantCulture));
        csv.Append("\r\nПрограмма;Отправлено байт;Получено байт;Всего байт\r\n");
        foreach (var program in report.Programs)
            csv.Append(Cell(Path.GetFileNameWithoutExtension(program.Path))).Append(';').Append(program.Upload).Append(';').Append(program.Download).Append(';').AppendLine(program.Total.ToString(CultureInfo.InvariantCulture));
        return csv.ToString();
    }

    private DayState Day(DateTime now) => days.TryGetValue(Key(now), out var state) ? state : days[Key(now)] = new DayState();
    private static string Key(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static TrafficDayStats EmptyDay(DateTime now) => new(Key(now), 0, 0, new long[24], new long[24], []);

    private void Trim(DateTime now)
    {
        var oldest = Key(now.Date.AddDays(-(MaxDays - 1)));
        foreach (var key in days.Keys.Where(key => string.CompareOrdinal(key, oldest) < 0 || string.CompareOrdinal(key, Key(now)) > 0).ToArray()) days.Remove(key);
        if (days.Count <= MaxDays) return;
        foreach (var key in days.Keys.Order(StringComparer.Ordinal).Take(days.Count - MaxDays).ToArray()) days.Remove(key);
    }
}

// upload/download у ядра накопительные для каждого соединения. В отчёт идёт только разница
// между снимками; новый идентификатор после перезапуска ядра начинает свой собственный счётчик.
public sealed class ProgramTrafficHistory
{
    private sealed record Seen(string Path, long Upload, long Download);
    private readonly Dictionary<string, Seen> active = new(StringComparer.Ordinal);

    public void Reset() => active.Clear();

    public void Record(IReadOnlyList<CoreConnection> connections, TrafficHistory history, DateTime now)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (connection.Id is not { Length: > 0 } id || string.IsNullOrWhiteSpace(connection.ProcessPath)) continue;
            seen.Add(id);
            var path = connection.ProcessPath;
            active.TryGetValue(id, out var old);
            var upload = old is null || connection.Upload < old.Upload || !string.Equals(path, old.Path, StringComparison.OrdinalIgnoreCase) ? connection.Upload : connection.Upload - old.Upload;
            var download = old is null || connection.Download < old.Download || !string.Equals(path, old.Path, StringComparison.OrdinalIgnoreCase) ? connection.Download : connection.Download - old.Download;
            active[id] = new(path, Math.Max(0, connection.Upload), Math.Max(0, connection.Download));
            if (upload > 0 || download > 0) Add(history, path, Math.Max(0, upload), Math.Max(0, download), now);
        }
        foreach (var id in active.Keys.Where(id => !seen.Contains(id)).ToArray()) active.Remove(id);
    }

    private static void Add(TrafficHistory history, string path, long upload, long download, DateTime now) =>
        history.AddProgram(path, upload, download, now);
}

public sealed class SpeedHistory
{
    private sealed class Bucket
    {
        public long Upload, Download;
        public int Count;
    }
    private readonly Dictionary<DateTime, Bucket> minutes = new();

    public void Reset() => minutes.Clear();

    public void Record(DateTime now, long upload, long download)
    {
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        if (!minutes.TryGetValue(minute, out var bucket)) minutes[minute] = bucket = new();
        bucket.Upload += Math.Max(0, upload); bucket.Download += Math.Max(0, download); bucket.Count++;
        foreach (var key in minutes.Keys.Where(key => key < minute.AddMinutes(-59)).ToArray()) minutes.Remove(key);
    }

    public AnalyticsPoint[] Report(DateTime now)
    {
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        return Enumerable.Range(0, 60).Select(i => minute.AddMinutes(i - 59)).Select(time =>
        {
            if (!minutes.TryGetValue(time, out var bucket) || bucket.Count == 0) return new AnalyticsPoint(time.ToString("HH:mm"), 0, 0);
            return new AnalyticsPoint(time.ToString("HH:mm"), bucket.Upload / bucket.Count, bucket.Download / bucket.Count);
        }).ToArray();
    }
}

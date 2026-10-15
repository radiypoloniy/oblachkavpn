using System.Globalization;

namespace Oblachka;

public sealed record SubscriptionMetadata(long? Upload, long? Download, long? Total, DateTimeOffset? Expires)
{
    // Заголовки панели, кроме subscription-userinfo. Текст приходит от сервера, поэтому
    // он обрезается по длине и очищается от управляющих символов; разметка не разбирается.
    public string? Title { get; init; }
    public string? Announce { get; init; }
    public TimeSpan? UpdateInterval { get; init; }
    // Маршрутизация сервиса из заголовка routing (формат Happ). Включается только с согласия.
    public RoutingProfile? Routing { get; init; }

    public const int MaxTextLength = 300;
    public static readonly TimeSpan MinUpdateInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxUpdateInterval = TimeSpan.FromDays(7);

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

    // Remnawave отдаёт profile-title и announce как «base64:<...>», иначе как обычный текст.
    public static string? DecodeText(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var value = header.Trim();
        if (value.Length > 4096) return null;
        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = value["base64:".Length..].Replace('-', '+').Replace('_', '/');
            var padding = encoded.Length % 4;
            if (padding != 0) encoded = encoded.PadRight(encoded.Length + 4 - padding, '=');
            try { value = new System.Text.UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded)); }
            catch (Exception ex) when (ex is FormatException or System.Text.DecoderFallbackException) { return null; }
        }
        return Clean(value);
    }

    // Текст сервера попадает в интерфейс, поэтому убираем управляющие символы и ограничиваем длину.
    public static string? Clean(string? value)
    {
        if (value is null) return null;
        var cleaned = new string(value.Where(c => !char.IsControl(c)).Take(MaxTextLength).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    // profile-update-interval в документации панели задан в днях.
    public static TimeSpan? ParseUpdateInterval(string? header)
    {
        if (!double.TryParse(header?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var days)) return null;
        if (double.IsNaN(days) || days <= 0 || days > 7) return null;
        var interval = TimeSpan.FromDays(days);
        return interval < MinUpdateInterval ? MinUpdateInterval : interval;
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
        // total=0 у Remnawave означает безлимит, а не исчерпанный объём.
        else if (Total == 0) parts.Add("Трафик без ограничения");
        return string.Join(" · ", parts);
    }

    // Короткая строка для главного экрана: без неё пользователь узнаёт срок только в настройках.
    public string ShortSummary(DateTimeOffset now)
    {
        if (Expires is { } expires)
        {
            if (expires < now) return "Подписка истекла";
            var left = expires - now;
            if (left < TimeSpan.FromDays(1)) return "Подписка истекает сегодня";
            if (left < TimeSpan.FromDays(14)) return $"Подписка ещё {(int)left.TotalDays} дн.";
            return "Подписка до " + expires.ToLocalTime().ToString("dd.MM.yyyy");
        }
        return Summary(now);
    }
}

public sealed class SubscriptionSchedule
{
    private DateTimeOffset due = DateTimeOffset.MinValue;
    private int failures;
    public bool IsDue(DateTimeOffset now) => now >= due;

    // Интервал задаёт панель заголовком profile-update-interval; без него — шесть часов.
    public void Complete(DateTimeOffset now, bool success, TimeSpan? interval = null)
    {
        failures = success ? 0 : Math.Min(failures + 1, 5);
        var period = interval ?? TimeSpan.FromHours(6);
        if (period < SubscriptionMetadata.MinUpdateInterval) period = SubscriptionMetadata.MinUpdateInterval;
        if (period > SubscriptionMetadata.MaxUpdateInterval) period = SubscriptionMetadata.MaxUpdateInterval;
        due = now.Add(success ? period : TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, failures - 1))));
    }
    public void AccessDenied() => due = DateTimeOffset.MaxValue;
}

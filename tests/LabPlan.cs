using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot;

public sealed record LabPlan(Uri Target, IReadOnlyList<Profile> Profiles, int Repetitions, int IntervalMilliseconds)
{
    public static LabPlan Parse(string text, bool allowLoopback = false)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > ProfilesLimit) throw new UserError("План стенда больше 1 МБ.");
        try
        {
            var root = JsonNode.Parse(text) as JsonObject ?? throw new UserError("Нужен JSON-объект плана стенда.");
            var keys = new[] { "target", "profiles", "repetitions", "interval_ms" };
            if (root.Any(p => !keys.Contains(p.Key))) throw new UserError("Неизвестное поле в плане стенда.");
            if (!Uri.TryCreate(root["target"]?.GetValue<string>(), UriKind.Absolute, out var target) ||
                target.UserInfo.Length > 0 || target.Fragment.Length > 0 ||
                !(target.Scheme == "https" || allowLoopback && target.Scheme == "http" && target.IsLoopback))
                throw new UserError("Стенду нужен HTTPS-адрес проверки без логина, пароля и фрагмента.");
            var count = root["repetitions"]?.GetValue<int>() ?? 3;
            var interval = root["interval_ms"]?.GetValue<int>() ?? 1000;
            if (count is < 1 or > 10 || interval is < 0 or > 10000)
                throw new UserError("Стенд: 1–10 повторов, интервал 0–10000 мс.");
            if (root["profiles"] is not JsonArray array || array.Count is < 1 or > 8)
                throw new UserError("Стенд: добавьте 1–8 профилей в массив profiles.");
            var profiles = array.Select(p => p is JsonValue value && value.TryGetValue<string>(out var link)
                ? RoutePilot.Profiles.Parse(link) : RoutePilot.Profiles.Parse(p?.ToJsonString() ?? "")).ToArray();
            return new(target, profiles, count, interval);
        }
        catch (UserError) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { throw new UserError("Некорректный план стенда. Проверьте типы полей и профили."); }
    }

    private const int ProfilesLimit = 1024 * 1024;
}

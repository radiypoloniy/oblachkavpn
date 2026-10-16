using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Oblachka;

// Какие серверы участвуют в «Автовыборе», переключении при обрыве и фоновом замере.
//
// Серверы «обхода белых списков» — каскады через российские адреса: дорогие и нужны для своей
// задачи. Их вход стоит рядом с человеком, поэтому замер до входа показывал единицы
// миллисекунд, и автовыбор ставил их первыми для всего подряд. По умолчанию они вне пула;
// человек может включить любой сервер в «Настройки → Автовыбор» или исключить.
// Хранятся только отличия от умолчания, по ключу из источника и имени: смена ключей
// подписки выбор не сбрасывает.
public sealed class AutoPool
{
    private static readonly Regex Whitelist = new(@"бел\w*\s+спис|white\s*-?\s*list|\bбс\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, bool> overrides = new(StringComparer.Ordinal);

    // Серверы для звонков и игр (обычно Hysteria2): самая низкая задержка, но не лучший канал
    // для всего остального. Тоже по умолчанию вне пула.
    private static readonly Regex Purpose = new(@"игр|звонк|\bgam(e|ing)\b|\bcalls?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsWhitelistBypass(Profile profile) => Whitelist.IsMatch(profile.Name);
    public static bool IsSpecialPurpose(Profile profile) => Purpose.IsMatch(profile.Name);
    public static bool DefaultIn(Profile profile) => !IsWhitelistBypass(profile) && !IsSpecialPurpose(profile);

    public static string Key(Profile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Source + ":" + profile.Name)));

    public bool Contains(Profile profile) => overrides.TryGetValue(Key(profile), out var chosen) ? chosen : DefaultIn(profile);

    public void Set(Profile profile, bool inPool)
    {
        if (inPool == DefaultIn(profile)) overrides.Remove(Key(profile));
        else overrides[Key(profile)] = inPool;
    }

    public void Load(IReadOnlyDictionary<string, bool>? saved)
    {
        overrides.Clear();
        foreach (var (key, value) in saved ?? new Dictionary<string, bool>())
            if (key.Length == 64 && key.All(char.IsAsciiHexDigit)) overrides[key] = value;
    }

    // Для сохранения: только отличия по серверам, которые ещё есть в списке.
    public Dictionary<string, bool> Snapshot(IEnumerable<Profile> live)
    {
        var keys = live.Select(Key).ToHashSet(StringComparer.Ordinal);
        return overrides.Where(o => keys.Contains(o.Key)).Take(Subscriptions.MaxProfiles).ToDictionary(o => o.Key, o => o.Value);
    }

    // Пул из списка; если человек исключил всё, автовыбор работает по всем — иначе ему не из чего выбрать.
    public Profile[] Members(IReadOnlyList<Profile> all)
    {
        var members = all.Where(Contains).ToArray();
        return members.Length > 0 ? members : all.ToArray();
    }
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoutePilot;

public sealed record ServerCountry(string? Code, string Name);

public static class CountryCatalog
{
    private static readonly Dictionary<string, string> Russian = Read("ru");
    private static readonly Dictionary<string, string> English = Read("en");
    private static readonly Dictionary<string, string> ShortNames = new()
    {
        ["EU"] = "Европа", ["NL"] = "Нидерланды", ["GB"] = "Великобритания", ["US"] = "США",
        ["RU"] = "Россия", ["KR"] = "Южная Корея", ["CZ"] = "Чехия", ["AE"] = "ОАЭ"
    };
    private static readonly Regex Prefix = new(@"^[\s\[\(]*([a-z]{2})(?=$|[\s\]\)_\-]|\p{IsCyrillic})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Dictionary<string, string> Read(string language)
    {
        using var stream = typeof(CountryCatalog).Assembly.GetManifestResourceStream($"RoutePilot.Assets.countries-{language}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!
            .Where(p => p.Key.Length == 2).ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value);
    }

    public static ServerCountry Resolve(string label)
    {
        var runes = label.EnumerateRunes().ToArray();
        for (var i = 0; i + 1 < runes.Length; i++)
        {
            var first = runes[i].Value - 0x1F1E6;
            var second = runes[i + 1].Value - 0x1F1E6;
            if (first is >= 0 and < 26 && second is >= 0 and < 26)
            {
                var flagged = FromCode($"{(char)('A' + first)}{(char)('A' + second)}");
                if (flagged.Code is not null) return flagged;
            }
        }
        var prefix = Prefix.Match(label);
        if (prefix.Success)
        {
            var coded = FromCode(prefix.Groups[1].Value);
            if (coded.Code is not null) return coded;
        }
        foreach (var pair in Russian.Concat(English).Concat(ShortNames).OrderByDescending(p => p.Value.Length))
            if (Regex.IsMatch(label, @"(?<!\p{L})" + Regex.Escape(pair.Value) + @"(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return FromCode(pair.Key);
        return new(null, "Сервер");
    }

    public static ServerCountry FromCode(string code)
    {
        code = code.ToUpperInvariant() switch { "UK" => "GB", var normalized => normalized };
        return Russian.TryGetValue(code, out var name)
            ? new(code, ShortNames.GetValueOrDefault(code, name))
            : new(null, "Сервер");
    }
}

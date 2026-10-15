using System.Text;
using System.Text.RegularExpressions;

namespace Oblachka;

public static class ServerLabels
{
    private static readonly Regex CodePrefix = new(@"^[\s\[\(]*([a-z]{2})(?=$|[\s\]\)_\-]|\p{IsCyrillic})[\s\]\)_\-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string DisplayName(string name)
    {
        var runes = name.EnumerateRunes().ToArray();
        var clean = new StringBuilder();
        for (var i = 0; i < runes.Length; i++)
        {
            if (i + 1 < runes.Length && IsRegional(runes[i]) && IsRegional(runes[i + 1])) { i++; continue; }
            clean.Append(runes[i]);
        }
        var label = clean.ToString().Trim();
        var prefix = CodePrefix.Match(label);
        if (prefix.Success && CountryCatalog.FromCode(prefix.Groups[1].Value).Code is not null)
            label = label[prefix.Length..].Trim();
        return label.Length > 0 ? label : CountryCatalog.Resolve(name).Name;
    }

    private static bool IsRegional(Rune rune) => rune.Value is >= 0x1F1E6 and <= 0x1F1FF;
}

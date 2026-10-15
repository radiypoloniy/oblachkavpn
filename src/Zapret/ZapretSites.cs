using System.Text.RegularExpressions;

namespace Oblachka;

// Домены из списков набора zapret (list-general, list-google): они идут мимо VPN, когда обход
// работает. Списки лежат в папке службы, окну они доступны на чтение.
public static class ZapretSites
{
    private static readonly Regex Domain = new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant);
    private static readonly string[] Lists = { "list-general.txt", "list-google.txt" };

    // lists — только эти файлы набора (группы ZapretPlan); null — все.
    public static string[] Read(string tag, IEnumerable<string>? lists = null)
    {
        if (tag.Length is 0 or > 40 || tag.Contains("..") || tag.IndexOfAny(new[] { '\\', '/' }) >= 0) return Array.Empty<string>();
        var folder = Path.Combine(ZapretInstaller.Root, tag, "lists");
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in Lists.Where(l => lists is null || lists.Contains(l)))
        {
            try { foreach (var line in File.ReadLines(Path.Combine(folder, name)).Take(2000)) if (Clean(line) is { } domain) result.Add(domain); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result.Take(RoutingProfile.MaxEntries).ToArray();
    }

    internal static string? Clean(string line)
    {
        var value = line.Split('#')[0].Trim().TrimStart('.').ToLowerInvariant();
        return Domain.IsMatch(value) ? value : null;
    }
}

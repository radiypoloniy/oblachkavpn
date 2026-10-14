using System.Net;
using System.Text;

namespace Oblachka;

// Разбор баз geosite.dat и geoip.dat (формат v2fly/Loyalsoldier — protobuf) без библиотек.
// Берутся только нужные категории: правило «geosite:category-ru» разворачивается в список
// доменов прямо в конфигурации ядра, поэтому ядру сами базы не нужны.
//
//   GeoSiteList { repeated GeoSite entry = 1 }   GeoSite { string country_code = 1; repeated Domain domain = 2 }
//   Domain { Type type = 1 (Plain, Regex, RootDomain, Full); string value = 2; repeated Attribute attribute = 3 }
//   GeoIPList  { repeated GeoIP entry = 1 }      GeoIP   { string country_code = 1; repeated CIDR cidr = 2 }
//   CIDR { bytes ip = 1; uint32 prefix = 2 }
public static class GeoDat
{
    public enum DomainKind { Keyword = 0, Regex = 1, Suffix = 2, Full = 3 }
    public sealed record SiteEntry(DomainKind Kind, string Value);

    public const long MaxFileBytes = 96L * 1024 * 1024;

    // Категории geosite: имя в нижнем регистре → домены. Неизвестные категории просто отсутствуют.
    public static Dictionary<string, List<SiteEntry>> ReadSites(byte[] data, IReadOnlyCollection<string> tags)
    {
        var wanted = new HashSet<string>(tags.Select(t => t.ToLowerInvariant()));
        var result = new Dictionary<string, List<SiteEntry>>();
        foreach (var (field, entry) in Messages(data, 0, data.Length))
        {
            if (field != 1) continue;
            string? code = null; var domains = new List<SiteEntry>();
            foreach (var (inner, value) in Messages(data, entry.Start, entry.End))
            {
                if (inner == 1) { code = Encoding.UTF8.GetString(data, value.Start, value.End - value.Start).ToLowerInvariant(); if (!wanted.Contains(code)) break; }
                else if (inner == 2 && code is not null) domains.Add(Domain(data, value));
            }
            if (code is not null && wanted.Contains(code)) result[code] = domains;
        }
        return result;
    }

    // Категории geoip: имя → диапазоны «a.b.c.d/n».
    public static Dictionary<string, List<string>> ReadIps(byte[] data, IReadOnlyCollection<string> tags)
    {
        var wanted = new HashSet<string>(tags.Select(t => t.ToLowerInvariant()));
        var result = new Dictionary<string, List<string>>();
        foreach (var (field, entry) in Messages(data, 0, data.Length))
        {
            if (field != 1) continue;
            string? code = null; var ranges = new List<string>();
            foreach (var (inner, value) in Messages(data, entry.Start, entry.End))
            {
                if (inner == 1) { code = Encoding.UTF8.GetString(data, value.Start, value.End - value.Start).ToLowerInvariant(); if (!wanted.Contains(code)) break; }
                else if (inner == 2 && code is not null && Cidr(data, value) is { } cidr) ranges.Add(cidr);
            }
            if (code is not null && wanted.Contains(code)) result[code] = ranges;
        }
        return result;
    }

    // Проверка, что файл — база нужного вида: хотя бы одна категория с непустым кодом.
    public static bool LooksValid(byte[] data)
    {
        try
        {
            foreach (var (field, entry) in Messages(data, 0, data.Length))
            {
                if (field != 1) return false;
                foreach (var (inner, value) in Messages(data, entry.Start, entry.End))
                    return inner == 1 && value.End > value.Start;
            }
            return false;
        }
        catch (FormatException) { return false; }
    }

    private static SiteEntry Domain(byte[] data, (int Start, int End) span)
    {
        var kind = DomainKind.Keyword; var value = "";
        var i = span.Start;
        while (i < span.End)
        {
            var key = Varint(data, ref i, span.End);
            var field = (int)(key >> 3);
            switch ((int)(key & 7))
            {
                case 0: var number = Varint(data, ref i, span.End); if (field == 1) kind = (DomainKind)Math.Min(number, 3); break;
                case 2:
                    var length = (int)Varint(data, ref i, span.End);
                    if (length < 0 || i + length > span.End) throw new FormatException();
                    if (field == 2) value = Encoding.UTF8.GetString(data, i, length);
                    i += length; break;
                default: throw new FormatException();
            }
        }
        return new(kind, value);
    }

    private static string? Cidr(byte[] data, (int Start, int End) span)
    {
        byte[]? ip = null; var prefix = -1;
        var i = span.Start;
        while (i < span.End)
        {
            var key = Varint(data, ref i, span.End);
            var field = (int)(key >> 3);
            switch ((int)(key & 7))
            {
                case 0: var number = Varint(data, ref i, span.End); if (field == 2) prefix = (int)Math.Min(number, 255); break;
                case 2:
                    var length = (int)Varint(data, ref i, span.End);
                    if (length < 0 || i + length > span.End) throw new FormatException();
                    if (field == 1) ip = data[i..(i + length)];
                    i += length; break;
                default: throw new FormatException();
            }
        }
        if (ip is not { Length: 4 or 16 }) return null;
        var max = ip.Length * 8;
        return prefix is >= 0 && prefix <= max ? new IPAddress(ip) + "/" + prefix : null;
    }

    // Поля длиной (тип 2) верхнего уровня участка: номер поля и границы значения.
    private static IEnumerable<(int Field, (int Start, int End) Span)> Messages(byte[] data, int start, int end)
    {
        var i = start;
        while (i < end)
        {
            var key = Varint(data, ref i, end);
            var field = (int)(key >> 3);
            switch ((int)(key & 7))
            {
                case 0: Varint(data, ref i, end); break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    var length = (int)Varint(data, ref i, end);
                    if (length < 0 || i + length > end) throw new FormatException("Повреждённая база адресов.");
                    yield return (field, (i, i + length));
                    i += length; break;
                default: throw new FormatException("Повреждённая база адресов.");
            }
        }
    }

    private static ulong Varint(byte[] data, ref int i, int end)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (i >= end) throw new FormatException("Повреждённая база адресов.");
            var b = data[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return result;
        }
        throw new FormatException("Повреждённая база адресов.");
    }
}

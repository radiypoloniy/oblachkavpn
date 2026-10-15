using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oblachka;

// Что именно запускается как zapret. Манифест лежит в репозитории (zapret/manifest.json), встроен
// в сборку и обновляется задачей GitHub из релизов Flowseal: стратегии и списки — сами, новый
// исполняемый файл — только после одобрения (запрос на слияние). Служба скачивает архив Flowseal
// с GitHub и сверяет архив и каждый файл с этими хешами.
public sealed record ZapretManifest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] string Tag,
    [property: JsonRequired] string Url,
    [property: JsonRequired] string Sha256,
    [property: JsonRequired] string Root,
    [property: JsonRequired] DateTimeOffset Updated,
    [property: JsonRequired] Dictionary<string, string> Files,
    [property: JsonRequired] ZapretStrategy[] Strategies)
{
    public const string Source = "https://github.com/Flowseal/zapret-discord-youtube/releases/download/";
    public const string RepositoryUrl = "https://raw.githubusercontent.com/" + AppUpdate.Repository + "/main/zapret/manifest.json";
    // Исполняемые файлы: их смена проходит через одобрение, остальное (стратегии, списки, подмены) — само.
    public static readonly string[] Executables = { "bin/winws.exe", "bin/WinDivert.dll", "bin/WinDivert64.sys", "bin/cygwin1.dll" };
    // Списки пользователя, на которые ссылаются стратегии Flowseal: их пишет служба, в архиве их нет.
    public static readonly string[] Generated = { "lists/list-general-user.txt", "lists/list-exclude-user.txt", "lists/ipset-exclude-user.txt" };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ZapretManifest Parse(string json)
    {
        ZapretManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ZapretManifest>(json, Json) ?? throw new UserError("zapret: пустой манифест."); }
        catch (JsonException) { throw new UserError("zapret: манифест повреждён."); }
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (Version != 1) throw new UserError("zapret: неизвестная версия манифеста.");
        if (Tag.Length is 0 or > 40 || !Tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) throw new UserError("zapret: неверная версия набора.");
        if (!Url.StartsWith(Source + Tag + "/", StringComparison.Ordinal) || !Url.EndsWith(".zip", StringComparison.Ordinal)) throw new UserError("zapret: архив не из релизов Flowseal.");
        if (!IsHash(Sha256) || Root.Length is 0 or > 80 || Root.Contains("..") || Root.Contains('\\') || Root.Contains('/')) throw new UserError("zapret: неверные сведения об архиве.");
        if (Files.Count is 0 or > 200 || Files.Any(f => !SafePath(f.Key) || !IsHash(f.Value))) throw new UserError("zapret: неверный список файлов.");
        if (Executables.Any(e => !Files.ContainsKey(e))) throw new UserError("zapret: в наборе нет winws и драйвера.");
        if (Strategies.Length is 0 or > 64 || Strategies.Select(s => s.Name).Distinct().Count() != Strategies.Length) throw new UserError("zapret: неверный список стратегий.");
        foreach (var strategy in Strategies)
        {
            ZapretArgs.Validate(strategy);
            if (ZapretArgs.Files(strategy).Any(f => !Files.ContainsKey(f) && !Generated.Contains(f))) throw new UserError("zapret: стратегия ссылается на файл не из набора.");
        }
    }

    // Порядок подбора: general, ALT, ALT2 … ALT13, затем FAKE TLS AUTO…, SIMPLE FAKE…, EXP и прочие.
    internal static (int Group, int Number, string Name) Rank(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
        var stem = name[..^digits.Length].TrimEnd();
        var alt = stem.EndsWith("ALT", StringComparison.Ordinal);
        var family = alt ? stem[..^3].TrimEnd() : stem;
        if (family.Length == 0) family = "ALT";
        var group = family switch { "general" => 0, "ALT" => 1, "FAKE TLS AUTO" => 2, "SIMPLE FAKE" => 3, "EXP" => 5, _ => 4 };
        return (group, alt ? (digits.Length > 0 ? int.Parse(digits) : 1) : 0, name);
    }

    internal static bool SafePath(string path) => path.Length is > 4 and <= 90 && (path.StartsWith("bin/", StringComparison.Ordinal) || path.StartsWith("lists/", StringComparison.Ordinal)) &&
        path.Count(c => c == '/') == 1 && !path.Contains("..") && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_');

    private static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    // Встроенный в сборку манифест — на случай, если GitHub недоступен.
    public static ZapretManifest Embedded()
    {
        using var stream = typeof(ZapretManifest).Assembly.GetManifestResourceStream("Oblachka.zapret.manifest.json") ?? throw new UserError("zapret: нет встроенного манифеста.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    // Сборка манифеста из архива Flowseal (задача обновления и проверки): стратегии из general*.bat,
    // хеши файлов из bin и lists, на которые они ссылаются, и исполняемых файлов.
    public static ZapretManifest FromArchive(string zipPath, string tag, string url)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var root = zip.Entries.Select(e => e.FullName.Split('/')[0]).Distinct().Single();
        var strategies = zip.Entries.Where(e => e.FullName.Count(c => c == '/') == 1 && e.Name.StartsWith("general", StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => Rank(ZapretStrategy.NameOf(e.Name)))
            .Select(e => { using var r = new StreamReader(e.Open()); return ZapretStrategy.Parse(ZapretStrategy.NameOf(e.Name), r.ReadToEnd()); }).ToArray();
        var needed = strategies.SelectMany(ZapretArgs.Files).Concat(Executables).Where(f => !Generated.Contains(f)).Distinct().ToArray();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in needed)
        {
            var entry = zip.GetEntry(root + "/" + path) ?? throw new UserError("zapret: в архиве нет " + path + ".");
            using var stream = entry.Open();
            files[path] = Convert.ToHexString(SHA256.HashData(stream));
        }
        using var whole = File.OpenRead(zipPath);
        var manifest = new ZapretManifest(1, tag, url, Convert.ToHexString(SHA256.HashData(whole)), root, DateTimeOffset.UtcNow, files, strategies);
        manifest.Validate();
        return manifest;
    }
}

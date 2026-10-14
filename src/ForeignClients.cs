using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oblachka;

// Подписка, найденная в другом VPN-клиенте на этом компьютере.
public sealed record ForeignSubscription(string Client, string Name, string Url)
{
    // «Throne · Рабочая»; если имени нет и оно равно хосту — только клиент: хост и так в подписи ниже.
    public string Title => Name == Host ? Client : Client + " · " + Name;
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : "";
    // Подпись под именем: хост и четыре последних знака адреса, чтобы различать две подписки
    // одного сервиса. Сам ключ на экран не выводится.
    public string Hint
    {
        get
        {
            var tail = Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.PathAndQuery.TrimEnd('/') : "";
            return tail.Length > 6 ? Host + " · …" + tail[^4..] : Host;
        }
    }
}

// Перенос подписок из других клиентов при первом запуске: человеку не нужно искать ссылку,
// если она уже есть в Happ, Throne, Hiddify и других.
//
// Читаются только адреса подписок, на этом компьютере и только по нажатию: найденное
// показывается, загружается выбранное. Хранилища клиентов (проверены на их файлах):
//   Throne       %APPDATA%\Throne\config\throne.db           groups.url (SQLite)
//   Hiddify      %APPDATA%\Hiddify\hiddify\db.sqlite          profile_entries.url (SQLite)
//   v2rayN       <папка программы>\guiConfigs\guiNDB.db       SubItem.Url (SQLite)
//   NekoBox      %LOCALAPPDATA%\nekobox\settings\groups\*.cfg адрес строкой UTF-16BE (QDataStream)
//   v2RayTun     %APPDATA%\v2RayTun.net\v2RayTun\shared_preferences.json  flutter.sub_*
//   FlClash(X)   %APPDATA%\com.follow\clash(x)\shared_preferences.json    flutter.config
//   Clash Verge  %APPDATA%\io.github.clash-verge-rev.clash-verge-rev\profiles.yaml
//   Happ         база зашифрована; адрес берётся из журнала обновления подписок.
// Берутся только HTTPS-адреса: подписку по HTTP клиент не загружает.
public static class ForeignClients
{
    public const int MaxResults = 12;

    public sealed record Roots(string AppData, string LocalAppData, string UserProfile, string ProgramFiles)
    {
        public static Roots Current => new(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    }

    public static IReadOnlyList<ForeignSubscription> Find(Roots? roots = null)
    {
        var r = roots ?? Roots.Current;
        var found = new List<ForeignSubscription>();
        void Scan(string client, Func<IEnumerable<(string? Name, string? Url)>> read)
        {
            try { foreach (var (name, url) in read()) Add(found, client, name, url); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        }
        Scan("Happ", () => Happ(Path.Combine(r.LocalAppData, "Happ", "logs", "subscription_log.txt")));
        Scan("Throne", () => Sqlite(Path.Combine(r.AppData, "Throne", "config", "throne.db"), "select name, url from groups where archive = 0"));
        Scan("Hiddify", () => Sqlite(Path.Combine(r.AppData, "Hiddify", "hiddify", "db.sqlite"), "select name, url from profile_entries"));
        foreach (var folder in V2rayNFolders(r))
            Scan("v2rayN", () => Sqlite(Path.Combine(folder, "guiConfigs", "guiNDB.db"), "select Remarks, Url from SubItem"));
        Scan("NekoBox", () => NekoBox(Path.Combine(r.LocalAppData, "nekobox", "settings", "groups")));
        Scan("v2RayTun", () => V2RayTun(Path.Combine(r.AppData, "v2RayTun.net", "v2RayTun", "shared_preferences.json")));
        foreach (var flavor in new[] { "clash", "clashx" })
            Scan(flavor == "clash" ? "FlClash" : "FlClashX", () => FlClash(Path.Combine(r.AppData, "com.follow", flavor, "shared_preferences.json")));
        Scan("Clash Verge", () => ClashVerge(Path.Combine(r.AppData, "io.github.clash-verge-rev.clash-verge-rev", "profiles.yaml")));
        return found.Take(MaxResults).ToArray();
    }

    private static void Add(List<ForeignSubscription> found, string client, string? name, string? url)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return;
        if (found.Any(f => string.Equals(f.Url, url, StringComparison.Ordinal))) return;
        name = string.IsNullOrWhiteSpace(name) || name.Contains("://", StringComparison.Ordinal) ? uri.Host : name.Trim();
        found.Add(new(client, name.Length > 60 ? name[..60] : name, url));
    }

    private static IEnumerable<(string?, string?)> Sqlite(string path, string sql) =>
        WinSqlite.Query(path, sql)?.Where(row => row.Length >= 2).Select(row => (row[0], row[1])) ?? Enumerable.Empty<(string?, string?)>();

    // «Subscription #1031376548 starting update from: https://…» — последний адрес каждой подписки.
    private static readonly Regex HappLine = new(@"Subscription #(\d+) starting update from: (https://\S+)", RegexOptions.CultureInvariant);
    private static IEnumerable<(string?, string?)> Happ(string log)
    {
        if (!File.Exists(log) || new FileInfo(log).Length > 8 * 1024 * 1024) return Enumerable.Empty<(string?, string?)>();
        var latest = new Dictionary<string, string>();
        foreach (Match match in HappLine.Matches(ReadShared(log))) latest[match.Groups[1].Value] = match.Groups[2].Value;
        return latest.Values.Reverse().Select(url => ((string?)null, (string?)url));
    }

    // Группы NekoBox — QDataStream: строки в UTF-16BE. Адрес подписки — первая строка https:// в файле.
    private static readonly Regex Https = new(@"https://[\x21-\x7E]+", RegexOptions.CultureInvariant);
    private static IEnumerable<(string?, string?)> NekoBox(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(folder, "*.cfg").Take(64))
        {
            if (new FileInfo(file).Length > 1024 * 1024) continue;
            var bytes = File.ReadAllBytes(file);
            foreach (var offset in new[] { 0, 1 })
            {
                var match = Https.Match(Encoding.BigEndianUnicode.GetString(bytes, offset, (bytes.Length - offset) & ~1));
                if (match.Success) { yield return (null, match.Value); break; }
            }
        }
    }

    private static IEnumerable<(string?, string?)> V2RayTun(string preferences)
    {
        if (Preferences(preferences) is not { } root) yield break;
        foreach (var (key, value) in root)
            if (key.StartsWith("flutter.sub_", StringComparison.Ordinal) && value?.GetValueKind() == JsonValueKind.String &&
                JsonNode.Parse(value.GetValue<string>()) is JsonObject sub)
                yield return (sub["remarks"]?.GetValue<string>(), sub["url"]?.GetValue<string>());
    }

    private static IEnumerable<(string?, string?)> FlClash(string preferences)
    {
        if (Preferences(preferences)?["flutter.config"] is not JsonValue config ||
            JsonNode.Parse(config.GetValue<string>())?["profiles"] is not JsonArray profiles) yield break;
        foreach (var item in profiles.OfType<JsonObject>())
            yield return (item["label"]?.GetValue<string>(), item["url"]?.GetValue<string>());
    }

    // profiles.yaml: элементы «- uid: …» с полями name: и url:. Разбор строк, без библиотеки YAML.
    private static IEnumerable<(string?, string?)> ClashVerge(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) yield break;
        string? name = null, url = null;
        foreach (var raw in ReadShared(path).Split('\n'))
        {
            var line = raw.Trim().TrimStart('-').Trim();
            if (line.StartsWith("uid:", StringComparison.Ordinal)) { if (url is not null) yield return (name, url); name = url = null; }
            else if (line.StartsWith("name:", StringComparison.Ordinal)) name = Unquote(line[5..]);
            else if (line.StartsWith("url:", StringComparison.Ordinal)) url = Unquote(line[4..]);
        }
        if (url is not null) yield return (name, url);
    }

    // v2rayN переносной: ищем его папку там, куда его обычно распаковывают.
    private static IEnumerable<string> V2rayNFolders(Roots r)
    {
        var places = new[] { r.LocalAppData, r.AppData, r.ProgramFiles, Path.Combine(r.UserProfile, "Desktop"), Path.Combine(r.UserProfile, "Downloads"), r.UserProfile, Path.GetPathRoot(r.UserProfile) ?? "C:\\" };
        foreach (var place in places.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] folders;
            try { folders = Directory.GetDirectories(place, "v2rayN*"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var folder in folders.Take(4))
            {
                yield return folder;
                // Архив v2rayN-windows-64.zip распаковывается во вложенную папку v2rayN.
                var nested = Path.Combine(folder, "v2rayN");
                if (Directory.Exists(nested)) yield return nested;
            }
        }
    }

    private static JsonObject? Preferences(string path) =>
        File.Exists(path) && new FileInfo(path).Length <= 8 * 1024 * 1024 ? JsonNode.Parse(ReadShared(path)) as JsonObject : null;

    private static string Unquote(string value) => value.Trim().Trim('"', '\'');

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

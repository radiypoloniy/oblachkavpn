using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Oblachka;

// Игры из лаунчеров. Их ярлыки — ссылки steam://, а не файлы, и в меню «Пуск» и реестре
// виден только лаунчер, поэтому игры ищутся по библиотекам самих лаунчеров.
//   Steam: папка из реестра, библиотеки — steamapps\libraryfolders.vdf, игры — appmanifest_*.acf.
//   Epic: манифесты C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item.
// Каждая находка — программа с папкой: правило берёт всю папку игры, а не один .exe.
public static class GameLibraries
{
    public sealed record Found(string Name, string Exe, string[] Folders);

    public static IEnumerable<Found> Steam()
    {
        var root = SteamRoot();
        if (root is null) return [];
        var libraries = new List<string> { root };
        try
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (var (key, value) in Vdf(File.ReadAllText(vdf)))
                    if (key == "path" && Directory.Exists(value) && !libraries.Contains(value, StringComparer.OrdinalIgnoreCase)) libraries.Add(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var result = new List<Found>();
        // Сам Steam — клиент, его служба, оверлей и игры всех библиотек: «Steam напрямую» должен
        // означать весь Steam, иначе часть трафика уходила бы мимо правила.
        var steamExe = Path.Combine(root, "steam.exe");
        if (File.Exists(steamExe))
            result.Add(new("Steam", steamExe, [root, .. libraries.Skip(1).Select(l => Path.Combine(l, "steamapps", "common")).Where(Directory.Exists)]));
        foreach (var library in libraries)
        {
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf").Take(500).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var manifest in manifests)
            {
                try
                {
                    var fields = Vdf(File.ReadAllText(manifest)).GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First().Value);
                    if (!fields.TryGetValue("name", out var name) || !fields.TryGetValue("installdir", out var dir)) continue;
                    var folder = Path.Combine(library, "steamapps", "common", dir);
                    // Служебные «игры» Steam: среды выполнения, а не то, что человек запускает.
                    if (name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)) continue;
                    if (MainExe(folder, name) is { } exe) result.Add(new(name, exe, [folder]));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return result;
    }

    public static IEnumerable<Found> Epic()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(folder)) return [];
        var result = new List<Found>();
        IEnumerable<string> items;
        try { items = Directory.EnumerateFiles(folder, "*.item").Take(500).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        foreach (var item in items)
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(item));
                string? Text(string name) => json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (Text("DisplayName") is not { } name || Text("InstallLocation") is not { } location || !Directory.Exists(location)) continue;
                var exe = Text("LaunchExecutable") is { Length: > 0 } launch ? Path.Combine(location, launch) : null;
                exe = exe is not null && File.Exists(exe) ? exe : MainExe(location, name);
                if (exe is not null) result.Add(new(name, exe, [location]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result;
    }

    private static string? SteamRoot()
    {
        foreach (var (hive, path, value) in new[] { (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"), (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"), (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath") })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key?.GetValue(value) is string text && text.Length > 3 && Directory.Exists(text)) return ActualCase(Path.GetFullPath(text.Replace('/', '\\')).TrimEnd('\\'));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        }
        return null;
    }

    // Файлы Valve KeyValues: строки вида "ключ"  "значение"; вложенность здесь не нужна.
    private static readonly Regex Pair = new("\"([^\"\\\\]+)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
    internal static IEnumerable<(string Key, string Value)> Vdf(string text) =>
        Pair.Matches(text).Select(m => (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Replace("\\\\", "\\")));

    // Главный .exe папки игры — для имени и значка. Сначала тот, чьё имя похоже на название игры
    // (Dota 2 → dota2.exe, а не vconsole2.exe из корня), затем ближе к корню, затем крупнее.
    internal static string? MainExe(string folder, string? title = null)
    {
        if (!Directory.Exists(folder)) return null;
        var key = Letters(title ?? Path.GetFileName(folder));
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(folder, "*.exe", options).Take(400)
                .Where(f => InstalledApps.Acceptable(Path.GetFileName(f), f) && !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => Similar(key, Letters(Path.GetFileNameWithoutExtension(f.Name))))
                .ThenBy(f => f.FullName.Count(c => c == '\\'))
                .ThenByDescending(f => f.Length).FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Letters(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // Сходство имени файла с названием: совпадение, начало одного в другом, иначе общий префикс от трёх букв.
    private static int Similar(string title, string file)
    {
        if (file.Length == 0 || title.Length == 0) return 0;
        if (title == file) return 1000;
        if (title.StartsWith(file, StringComparison.Ordinal) || file.StartsWith(title, StringComparison.Ordinal)) return 500 + Math.Min(file.Length, title.Length);
        var common = 0;
        while (common < Math.Min(title.Length, file.Length) && title[common] == file[common]) common++;
        return common >= 3 ? common : 0;
    }

    // Steam хранит путь строчными буквами — в правиле и подписи нужен настоящий.
    internal static string ActualCase(string path)
    {
        try
        {
            var parts = path.Split(Path.DirectorySeparatorChar);
            var current = parts[0].ToUpperInvariant() + Path.DirectorySeparatorChar;
            foreach (var part in parts.Skip(1))
                current = Directory.EnumerateFileSystemEntries(current, part).FirstOrDefault() ?? Path.Combine(current, part);
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
    }
}

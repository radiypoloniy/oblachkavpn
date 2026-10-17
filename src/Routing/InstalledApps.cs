using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Oblachka;

// Программа на этом компьютере для выбора в правилах: человеческое имя, имя файла и путь (для значка).
// Folders — папки, которые правило берёт целиком (игра лаунчера, Steam со всеми библиотеками);
// без них — папка самой программы (ProgramRules).
public sealed record InstalledApp(string Name, string Executable, string Path, bool Running)
{
    public string[]? Folders { get; init; }
}

// Каталог программ для «Правила → Программы». Источники, от лучшего имени к худшему:
//   1. ярлыки меню «Пуск» (общие и пользователя) — имя ярлыка и есть то, что человек знает;
//   2. установленные программы из реестра (Uninstall: DisplayName и DisplayIcon);
//   3. запущенные сейчас процессы — описание файла из его версии.
// Установщики, деинсталляторы и системные файлы Windows отбрасываются.
public static class InstalledApps
{
    public const int MaxApps = 600;

    public static IReadOnlyList<InstalledApp> Find()
    {
        // По полному пути: у разных игр бывает один и тот же Game.exe.
        var found = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name, string? path, bool running = false, bool addNew = true, string[]? folders = null)
        {
            if (string.IsNullOrWhiteSpace(name) || Clean(path) is not { } exe) return;
            var file = System.IO.Path.GetFileName(exe);
            if (!Acceptable(file, exe)) return;
            if (found.TryGetValue(exe, out var known))
            {
                if (running && !known.Running) found[exe] = known with { Running = true };
                return;
            }
            if (addNew && found.Count < MaxApps) found[exe] = new(CleanName(name), file, exe, running) { Folders = folders };
        }
        // Лаунчеры первыми: у игры из библиотеки имя и папка точнее, чем у ярлыка.
        foreach (var game in GameLibraries.Steam().Concat(GameLibraries.Epic())) Add(game.Name, game.Exe, folders: game.Folders);
        foreach (var (name, target) in StartMenu()) Add(name, target);
        foreach (var (name, target) in Registry()) Add(name, target);
        // Запущенное без окна (службы, фоновые помощники) только отмечается, в список не добавляется.
        foreach (var (name, target, window) in Processes()) Add(name, target, running: true, addNew: window);
        return found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    // Поиск по первым буквам: «тел», «tel» и «еуд» (не та раскладка) находят Telegram, «yand» —
    // «Яндекс Музыку». Выше — совпадение с началом имени, затем с началом слова, затем файла.
    public static IReadOnlyList<InstalledApp> Search(IReadOnlyList<InstalledApp> apps, string query)
    {
        query = query.Trim().ToLowerInvariant();
        if (query.Length == 0) return apps;
        var queries = new[] { query, Translit(query), SwapLayout(query) };
        int Score(InstalledApp app)
        {
            var best = -1;
            foreach (var name in new[] { app.Name.ToLowerInvariant(), Translit(app.Name.ToLowerInvariant()) })
            for (var variant = 0; variant < queries.Length; variant++)
            {
                var q = queries[variant];
                var score = name.StartsWith(q, StringComparison.Ordinal) ? 0
                    : name.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(q, StringComparison.Ordinal)) ? 1
                    : app.Executable.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 2
                    : name.Contains(q, StringComparison.Ordinal) || app.Executable.Contains(q, StringComparison.OrdinalIgnoreCase) ? 3 : -1;
                // Набранное как есть и транслит выше, чем догадка о раскладке: «т» — это Telegram, а не NekoBox.
                if (score >= 0) score = score * 3 + variant;
                if (score >= 0 && (best < 0 || score < best)) best = score;
            }
            return best;
        }
        return apps.Select(a => (a, s: Score(a))).Where(x => x.s >= 0).OrderBy(x => x.s)
            .ThenBy(x => x.a.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => x.a).ToArray();
    }

    private static readonly char[] WordBreaks = { ' ', '-', '_', '.', '(', ')' };
    private const string Cyrillic = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
    private static readonly string[] Latin = { "a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "h", "ts", "ch", "sh", "sch", "", "y", "", "e", "yu", "ya" };
    private const string RussianKeys = "йцукенгшщзхъфывапролджэячсмитьбю";
    private const string EnglishKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.";

    internal static string Translit(string text) =>
        string.Concat(text.Select(c => Cyrillic.IndexOf(c) is var i and >= 0 ? Latin[i] : c.ToString()));

    // Набрано не в той раскладке: «еуд» → «tel», «ntktuhfv» → «телеграм».
    internal static string SwapLayout(string text) => string.Concat(text.Select(c =>
        RussianKeys.IndexOf(c) is var r and >= 0 ? EnglishKeys[r] : EnglishKeys.IndexOf(c) is var e and >= 0 ? RussianKeys[e] : c));

    private static readonly string[] NotApps = { "unins", "uninst", "setup", "install", "remove", "crashpad", "crashreport", "helper", "elevate",
        "notification_helper", "maintenanceservice", "vc_redist", "dotnet-", "windowsdesktop-runtime" };
    // Свои: правило на окно или ядра Облачков сломало бы сам туннель.
    private static readonly string[] Own = { "oblachka.exe", "oblachka.tests.exe", "xray.exe", "sing-box.exe" };
    // Кэши установщиков: DisplayIcon в реестре часто указывает туда, на сам установщик.
    private static readonly string[] NotAppFolders = { @"\package cache\", @"\installer\", @"\windowsapps\microsoft.", @"\temp\" };

    // «WinSCP 6.5.5» → «WinSCP», «Inno Setup, версия 6.7.3» → «Inno Setup», «Python 3.14.3 (64-bit)» → «Python».
    private static readonly System.Text.RegularExpressions.Regex Version =
        new(@"^(.+?)[\s,]+(?:версия\s+|version\s+|v)?\d+(?:\.\d+)+.*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static string CleanName(string name)
    {
        name = name.Trim();
        var match = Version.Match(name);
        if (!match.Success) return name;
        var cut = match.Groups[1].Value.Trim();
        // «IDLE (Python 3.14 64-bit)»: версия была внутри скобок — незакрытая скобка уходит.
        var open = cut.LastIndexOf('(');
        if (open >= 0 && cut.IndexOf(')', open) < 0) cut = cut[..open].Trim();
        return cut.Length >= 2 ? cut : name;
    }

    internal static bool Acceptable(string file, string path)
    {
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var lower = file.ToLowerInvariant();
        if (NotApps.Any(lower.Contains) || Own.Contains(lower)) return false;
        var folder = path.ToLowerInvariant();
        if (NotAppFolders.Any(folder.Contains)) return false;
        try { ProgramRoutingPlan.Create(file, SiteAction.Tunnel); } catch (UserError) { return false; }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // Встроенные программы Windows (explorer, svchost) правилами не настраивают.
        return !path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // «"C:\app\x.exe",0» → C:\app\x.exe; только существующий файл .exe.
    internal static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var text = Environment.ExpandEnvironmentVariables(path.Trim());
        if (text.StartsWith('"')) { var end = text.IndexOf('"', 1); text = end > 0 ? text[1..end] : text.Trim('"'); }
        var comma = text.LastIndexOf(',');
        if (comma > 0 && !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) text = text[..comma];
        text = text.Trim();
        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(text) ? text : null;
    }

    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
    private static readonly EnumerationOptions TopLevel = new() { IgnoreInaccessible = true };

    private static IEnumerable<(string, string?)> StartMenu()
    {
        // Меню «Пуск» — со всеми подпапками; рабочий стол — только верхний уровень: там бывают огромные папки.
        var roots = new[] { (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Recursive), (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Recursive),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), TopLevel), (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), TopLevel) };
        var result = new List<(string, string?)>();
        foreach (var (root, depth) in roots.Where(r => Directory.Exists(r.Item1)))
        {
            IEnumerable<string> links;
            // Недоступная подпапка не должна обрывать перебор всего меню.
            try { links = Directory.EnumerateFiles(root, "*.lnk", depth).Take(1500).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var link in links)
            {
                try
                {
                    var (target, arguments) = ShellLinks.Read(link);
                    // Discord, Slack и другие на Squirrel: ярлык ведёт на Update.exe --processStart Discord.exe.
                    var marker = arguments.IndexOf("--processStart", StringComparison.OrdinalIgnoreCase);
                    if (marker >= 0 && target is not null)
                    {
                        var real = arguments[(marker + 14)..].Trim().Split(' ', 2)[0].Trim('"');
                        target = Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(target)!, real, Recursive).FirstOrDefault() ?? target;
                    }
                    result.Add((System.IO.Path.GetFileNameWithoutExtension(link), target));
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException) { }
            }
        }
        return result;
    }

    private static IEnumerable<(string?, string?)> Registry()
    {
        var keys = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default)
        };
        var result = new List<(string?, string?)>();
        foreach (var (hive, view) in keys)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames().Take(1500))
                {
                    using var app = root.OpenSubKey(name);
                    if (app is null || app.GetValue("SystemComponent") is 1) continue;
                    result.Add((app.GetValue("DisplayName") as string, app.GetValue("DisplayIcon") as string));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return result;
    }

    private static IEnumerable<(string?, string?, bool)> Processes()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var result = new List<(string?, string?, bool)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null) continue;
                    var info = FileVersionInfo.GetVersionInfo(path);
                    var name = new[] { info.FileDescription, process.ProcessName }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                    result.Add((name, path, process.MainWindowHandle != IntPtr.Zero));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or FileNotFoundException) { }
            }
        }
        return result;
    }
}

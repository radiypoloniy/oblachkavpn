using System.Text;
using System.Text.Json.Nodes;

namespace Oblachka;

// Правило программы. Три вида:
//   только Executable — любая программа с этим именем файла где угодно (старые правила);
//   Executable + Folder — именно этот файл в этой папке;
//   Executable + Folder + AllInFolder — всё, что запускается из папки и её подпапок: клиент, его
//     службы, помощники и игры (Steam — это не только steam.exe). Executable тогда — главная
//     программа папки, по ней показываются имя и значок.
public sealed record ProgramRule(string Executable, SiteAction Action)
{
    public string? Folder { get; init; }
    public bool AllInFolder { get; init; }

    // По этому ключу правила не повторяются: одно имя, один файл или одна папка — одно правило.
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => Folder is null ? Executable : AllInFolder ? Folder + "\\" : Folder + "\\" + Executable;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FilePath => Folder is null ? null : Folder + "\\" + Executable;
    public override string ToString() => (Folder is null ? Executable : AllInFolder ? Folder + "\\…" : FilePath) + " — " + new SiteRule("", Action).Label;
}

public static class ProgramRoutingPlan
{
    public static ProgramRule Create(string executable, SiteAction action)
    {
        executable = executable.Trim();
        if (!Enum.IsDefined(action) || executable.Length is < 5 or > 120 || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains("..") || executable.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' or '(' or ')' or '+')))
            throw new UserError("Укажите имя программы, например steam.exe, без пути к файлу.");
        return new(executable, action);
    }

    // Файл или папка программы. Слишком широкие папки (диск, Windows, Program Files, профиль
    // пользователя) целиком не принимаются: правило «напрямую» на них увело бы мимо VPN почти всё.
    public static ProgramRule Create(string path, SiteAction action, bool allInFolder)
    {
        var folder = NormalizeFolder(Path.GetDirectoryName(path?.Trim() ?? ""), allInFolder);
        var rule = Create(Path.GetFileName(path!.Trim()), action) with { Folder = folder, AllInFolder = allInFolder };
        return rule;
    }

    // whole — правило на всю папку: только тогда широкие папки запрещены. Один точный файл можно
    // указать где угодно, хоть в «Загрузках».
    public static string NormalizeFolder(string? folder, bool whole)
    {
        folder = folder?.Trim().TrimEnd('\\');
        if (string.IsNullOrEmpty(folder) || folder.Length > 200 || folder.Length < 4 || !char.IsAsciiLetter(folder[0]) || folder[1] != ':' || folder[2] != '\\' ||
            folder[2..].Any(c => char.IsControl(c) || c is '/' or '*' or '?' or '"' or '<' or '>' or '|' or ':') ||
            folder.Split('\\').Skip(1).Any(s => s.Length == 0 || s.Trim() != s || s.Trim('.').Length == 0))
            throw new UserError("Папка программы должна быть полным путём на диске, например C:\\Games\\Steam.");
        if (whole && TooBroad(folder)) throw new UserError("Эта папка слишком общая: правило задело бы почти все программы. Выберите папку самой программы.");
        return char.ToUpperInvariant(folder[0]) + folder[1..];
    }

    // Не по окружению, а по виду пути: служба работает от SYSTEM и чужих профилей не знает.
    internal static bool TooBroad(string folder)
    {
        var parts = folder.Split('\\').Skip(1).Select(p => p.ToLowerInvariant()).ToArray();
        if (parts.Length == 0 || parts[0] == "windows") return true;
        string[] system = ["program files", "program files (x86)", "programdata", "users", "games", "programs"];
        if (parts.Length == 1) return system.Contains(parts[0]);
        if (parts[0] is "program files" or "program files (x86)") return parts.Length == 2 && parts[1] is "common files" or "windowsapps";
        if (parts[0] != "users") return false;
        // C:\Users\<имя>, его AppData, AppData\Local|Roaming|LocalLow, Local\Programs, Рабочий стол и загрузки.
        if (parts.Length == 2) return true;
        string[] personal = ["appdata", "desktop", "downloads", "documents", "onedrive", "saved games"];
        if (parts.Length == 3) return personal.Contains(parts[2]);
        if (parts[2] != "appdata") return false;
        if (parts.Length == 4) return parts[3] is "local" or "roaming" or "locallow";
        return parts.Length == 5 && parts[3] == "local" && parts[4] is "programs" or "temp" or "packages";
    }

    public static void Validate(IReadOnlyList<ProgramRule>? rules)
    {
        if (rules is null || rules.Count > 100) throw new UserError("Допускается до 100 правил программ.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule is null || Create(rule.Executable, rule.Action).Executable != rule.Executable || !seen.Add(rule.Key))
                throw new UserError("Правила программ повреждены или повторяются.");
            if (rule.Folder is not null && NormalizeFolder(rule.Folder, rule.AllInFolder) != rule.Folder) throw new UserError("Правила программ повреждены или повторяются.");
            if (rule.Folder is null && rule.AllInFolder) throw new UserError("Правила программ повреждены или повторяются.");
        }
    }

    // Порядок в ядре: первое совпадение побеждает. Сначала точные файлы, потом имена, потом
    // папки — более глубокая раньше: правило на игру из библиотеки Steam сильнее правила на весь Steam.
    public static IEnumerable<ProgramRule> Ordered(IEnumerable<ProgramRule> rules) => rules
        .OrderBy(r => r.Folder is null ? 1 : r.AllInFolder ? 2 : 0)
        .ThenByDescending(r => r.Folder?.Count(c => c == '\\') ?? 0)
        .ThenByDescending(r => r.Folder?.Length ?? 0);

    // Какое правило ядро применит к программе по этому пути: первое совпадение в том же порядке.
    public static ProgramRule? Find(IEnumerable<ProgramRule> rules, string path) => Ordered(rules).FirstOrDefault(r =>
        r.Folder is null ? string.Equals(Path.GetFileName(path), r.Executable, StringComparison.OrdinalIgnoreCase)
        : r.AllInFolder ? path.StartsWith(r.Folder + "\\", StringComparison.OrdinalIgnoreCase)
        : string.Equals(path, r.FilePath, StringComparison.OrdinalIgnoreCase));

    // Условие правила ядра sing-box. Путь процесса Windows сравнивается без учёта регистра.
    public static JsonObject Match(ProgramRule rule) => rule.Folder is null
        ? new JsonObject { ["process_name"] = new JsonArray(rule.Executable) }
        : new JsonObject { ["process_path_regex"] = new JsonArray("(?i)^" + Re2(rule.Folder + "\\") + (rule.AllInFolder ? "" : Re2(rule.Executable) + "$")) };

    // Экранирование для RE2 (Go): Regex.Escape из .NET экранирует и пробел, а RE2 такого не принимает.
    internal static string Re2(string text)
    {
        var result = new StringBuilder(text.Length * 2);
        foreach (var c in text) { if (@"\.+*?()|[]{}^$".Contains(c)) result.Append('\\'); result.Append(c); }
        return result.ToString();
    }

    // Правила программ применяются в ядре системного туннеля: SingBoxTunConfig строит их
    // из аренды сессии. Здесь — та же сборка для проверки черновика из окна.
    public static JsonObject Compile(NetworkLeaseRequest request, IReadOnlyList<ProgramRule> rules)
    {
        Validate(rules);
        return SingBoxTunConfig.Create(request with { Programs = rules.ToArray() });
    }
}

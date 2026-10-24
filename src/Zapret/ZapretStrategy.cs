using System.Text;

namespace Oblachka;

// Стратегия zapret: имя и аргументы winws с подстановками {BIN} и {LISTS} — папками
// бинарников и списков, известными только службе. Берётся из .bat набора Flowseal:
//   start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=… ^
//   --filter-udp=443 --hostlist="%LISTS%list-general.txt" … --new ^ …
// Разбор — только эта строка: экранирование ^, кавычки, %BIN%, %LISTS% и игровой фильтр
// (%GameFilterTCP% / %GameFilterUDP% — по умолчанию у Flowseal выключен, порт-заглушка 12).
public sealed record ZapretStrategy(string Name, string[] Args)
{
    public const string Bin = "{BIN}", Lists = "{LISTS}";
    private const string GameFilterOff = "12";

    // Сшитая стратегия «A + B»: YouTube — из A, всё остальное (Discord, общий список, QUIC) — из B.
    public const string Joiner = " + ";
    private const string GoogleList = "--hostlist=" + Lists + "list-google.txt";

    // Стратегии Flowseal — блоки, разделённые --new: свой блок у YouTube (list-google.txt), у Discord
    // (discord.media, его голос, общий список). Бывает, что одна стратегия открывает YouTube, а другая —
    // Discord; тогда блок YouTube берётся из первой, остальные — из второй. Склеиваются только
    // стратегии с одинаковым устройством блоков (фильтры совпадают по порядку); ALT5 с одним блоком
    // на весь TCP — нет. null — не склеить. Аргументы берутся только из проверенных стратегий.
    public static ZapretStrategy? Combine(ZapretStrategy youtube, ZapretStrategy rest)
    {
        var a = Blocks(youtube.Args); var b = Blocks(rest.Args);
        var google = Array.FindIndex(b, block => block.Contains(GoogleList));
        if (youtube.Name == rest.Name || a.Length != b.Length || google < 0 || !a[google].Contains(GoogleList) ||
            a.Zip(b).Any(pair => !Filters(pair.First).SequenceEqual(Filters(pair.Second)))) return null;
        var blocks = b.Select((block, i) => i == google ? a[i] : block);
        return new(youtube.Name + Joiner + rest.Name, blocks.SelectMany((block, i) => i == 0 ? block : block.Prepend("--new")).ToArray());
    }

    private static string[][] Blocks(string[] args)
    {
        var blocks = new List<List<string>> { new() };
        foreach (var arg in args) { if (arg == "--new") blocks.Add(new()); else blocks[^1].Add(arg); }
        return blocks.Select(b => b.ToArray()).ToArray();
    }

    // Что ловит блок: перехват, фильтры, списки — без способа обхода.
    private static IEnumerable<string> Filters(string[] block) =>
        block.Where(a => a.StartsWith("--wf-", StringComparison.Ordinal) || a.StartsWith("--filter-", StringComparison.Ordinal) ||
            a.StartsWith("--hostlist", StringComparison.Ordinal) || a.StartsWith("--ipset", StringComparison.Ordinal));

    // Имя файла general (ALT3).bat → «ALT3», general.bat → «general».
    public static string NameOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        var open = stem.IndexOf('(');
        return open >= 0 && stem.EndsWith(')') ? stem[(open + 1)..^1].Trim() : stem;
    }

    public static ZapretStrategy Parse(string name, string bat)
    {
        // Строки, продолженные «^» в конце, склеиваются в одну команду.
        var joined = bat.Replace("\r", "").Replace("^\n", " ");
        var line = joined.Split('\n').FirstOrDefault(l => l.Contains("winws.exe\"", StringComparison.OrdinalIgnoreCase))
            ?? throw new UserError("В стратегии нет запуска winws.exe.");
        var tail = line[(line.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase) + "winws.exe\"".Length)..];
        var args = Tokens(tail).Select(Substitute).Where(a => a.Length > 0).ToArray();
        if (args.Length == 0) throw new UserError("Стратегия без аргументов.");
        return new(name, args);
    }

    private static string Substitute(string token) => token
        .Replace("%BIN%", Bin, StringComparison.OrdinalIgnoreCase).Replace("%LISTS%", Lists, StringComparison.OrdinalIgnoreCase)
        .Replace("%GameFilterTCP%", GameFilterOff, StringComparison.OrdinalIgnoreCase).Replace("%GameFilterUDP%", GameFilterOff, StringComparison.OrdinalIgnoreCase)
        .Replace("%GameFilter%", GameFilterOff, StringComparison.OrdinalIgnoreCase);

    // Аргументы командной строки cmd: пробелы делят, кавычки склеивают, «^x» — буквальный x.
    private static IEnumerable<string> Tokens(string text)
    {
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '^' && !quoted && i + 1 < text.Length) { current.Append(text[++i]); continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}

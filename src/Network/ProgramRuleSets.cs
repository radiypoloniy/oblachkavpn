using System.Text.Json.Nodes;

namespace Oblachka;

// Правила программ, которые меняются без переподключения. Новая сессия на время запуска снимает
// защиту, поэтому пересоздавать её ради одного клика в «Соединениях» нельзя. Вместо этого ядро
// туннеля ссылается на файлы правил (rule-set), а служба их переписывает — ядро перечитывает
// их само, как список прямых сайтов (DirectDns).
//
// Порядок правил важен (первое совпадение побеждает): игра сильнее всего Steam, точный файл
// сильнее имени. Файлов — уровни × действия; правило попадает на уровень после всех правил,
// которые обязаны сработать раньше него. Внутри уровня правила не пересекаются, и порядок
// действий там не важен.
// Sites — правила сайтов для всех программ (SiteRuleSets): тоже на ходу. null — прежнее окно без них.
public sealed record ProgramRulesRequest(ProgramRule[] Rules, SiteRule[]? Sites = null)
{
    public void Validate()
    {
        ProgramRoutingPlan.Validate(Rules);
        if (Sites is not null) RoutingPlan.Validate(Sites);
    }
}

public static class ProgramRuleSets
{
    public const int Levels = 6;
    private static readonly SiteAction[] Actions = [SiteAction.Block, SiteAction.Direct, SiteAction.Tunnel];
    private static readonly object Gate = new();

    public static string Folder => NetworkHelperHost.DataPath("programs");
    public static string Tag(int level, SiteAction action) => "programs-" + level + "-" + action.ToString().ToLowerInvariant();

    // Правила, которые сейчас в файлах: с ними сравнивается новый набор (ConnectionReset).
    private static ProgramRule[] current = [];

    public static void Accept(ProgramRulesRequest request)
    {
        request.Validate();
        var before = current;
        Write(Folder, request.Rules);
        if (request.Sites is not null) SiteRuleSets.Write(SiteRuleSets.Folder, request.Sites);
        // Ядро перечитывает файлы не мгновенно: обрываем соединения, когда новые правила уже
        // действуют, иначе программа переподключилась бы прежним путём.
        var after = request.Rules;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            try { ConnectionReset.Apply(before, after); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        });
    }

    // Все файлы есть всегда, пустые — с заглушкой: ядро не запускается со ссылкой на отсутствующий файл.
    public static void Write(string folder, IReadOnlyList<ProgramRule> rules)
    {
        ProgramRoutingPlan.Validate(rules);
        var placed = Place(rules);
        lock (Gate)
        {
            current = rules.ToArray();
            Directory.CreateDirectory(folder);
            for (var level = 0; level < Levels; level++)
                foreach (var action in Actions)
                {
                    var matches = placed.Where(p => p.Level == level && p.Rule.Action == action).Select(p => (JsonNode)ProgramRoutingPlan.Match(p.Rule)).ToArray();
                    var json = new JsonObject
                    {
                        ["version"] = 2,
                        ["rules"] = matches.Length > 0 ? new JsonArray(matches) : new JsonArray(new JsonObject { ["process_name"] = new JsonArray("oblachka-none.exe") })
                    }.ToJsonString();
                    var path = Path.Combine(folder, Tag(level, action) + ".json");
                    // Без лишней записи: ядро перечитывает файл по изменению.
                    if (File.Exists(path) && File.ReadAllText(path) == json) continue;
                    File.WriteAllText(path + ".tmp", json);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
        }
    }

    // Ссылки на файлы в конфигурации ядра туннеля; route — правило для действия (как у прочих правил).
    public static void AddTo(JsonObject config, string folder, Func<JsonObject, SiteAction, JsonObject> route)
    {
        var sets = config["route"]!["rule_set"] as JsonArray ?? new JsonArray();
        config["route"]!["rule_set"] = sets;
        var rules = (JsonArray)config["route"]!["rules"]!;
        for (var level = 0; level < Levels; level++)
            foreach (var action in Actions)
            {
                sets.Add(new JsonObject { ["type"] = "local", ["tag"] = Tag(level, action), ["format"] = "source", ["path"] = Path.Combine(folder, Tag(level, action) + ".json") });
                rules.Add(route(new JsonObject { ["rule_set"] = new JsonArray(Tag(level, action)) }, action));
            }
    }

    // Уровень правила: 0, если ничто не обязано сработать раньше; иначе на один выше самого
    // высокого из таких. Слишком глубокая вложенность ложится на последний уровень.
    internal static IReadOnlyList<(ProgramRule Rule, int Level)> Place(IReadOnlyList<ProgramRule> rules)
    {
        var ordered = ProgramRoutingPlan.Ordered(rules).ToArray();
        var levels = new int[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var level = 0;
            for (var j = 0; j < i; j++)
                if (Before(ordered[j], ordered[i])) level = Math.Max(level, levels[j] + 1);
            levels[i] = Math.Min(level, Levels - 1);
        }
        return ordered.Select((r, i) => (r, levels[i])).ToArray();
    }

    // Обязано ли правило a сработать раньше b: они могут поймать один процесс, и a точнее.
    // Правило с сайтом — раньше правила той же программы без сайта и раньше правила с более общим
    // сайтом; правило без сайта правило с сайтом не опережает.
    internal static bool Before(ProgramRule a, ProgramRule b)
    {
        if (a.Domain is not null || b.Domain is not null)
        {
            if (a.Domain is null) return false;
            var plainA = a with { Domain = null }; var plainB = b with { Domain = null };
            var overlap = plainA.TargetKey.Equals(plainB.TargetKey, StringComparison.OrdinalIgnoreCase) || Before(plainA, plainB) || Before(plainB, plainA);
            if (!overlap) return false;
            if (b.Domain is null) return true;
            return a.Domain.EndsWith("." + b.Domain, StringComparison.Ordinal) ||
                a.Domain == b.Domain && !plainA.TargetKey.Equals(plainB.TargetKey, StringComparison.OrdinalIgnoreCase) && Before(plainA, plainB);
        }

        static bool Inside(string folder, string outer) =>
            string.Equals(folder, outer, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(outer + "\\", StringComparison.OrdinalIgnoreCase);
        var aExact = a.Folder is not null && !a.AllInFolder;
        if (b.Folder is null) return aExact && string.Equals(a.Executable, b.Executable, StringComparison.OrdinalIgnoreCase);
        if (!b.AllInFolder) return false;
        // Имя может оказаться где угодно, в том числе в папке b.
        if (a.Folder is null) return true;
        return aExact ? Inside(a.Folder, b.Folder) : !string.Equals(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase) && Inside(a.Folder, b.Folder);
    }
}

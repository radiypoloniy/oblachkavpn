using System.Text.Json.Nodes;

namespace Oblachka;

// Правила сайтов для всех программ — как правила программ (ProgramRuleSets): файлы rule-set, которые
// служба переписывает, а ядро туннеля перечитывает само. Правило сайта меняется на ходу, без
// переподключения: «YouTube напрямую» из «Сейчас в сети» действует сразу.
//
// Первое совпадение побеждает, поэтому music.youtube.com должен сработать раньше youtube.com.
// Файлов — уровни × действия; правило ложится на уровень после всех правил, которые его накрывают.
public static class SiteRuleSets
{
    public const int Levels = 4;
    private static readonly SiteAction[] Actions = [SiteAction.Block, SiteAction.Direct, SiteAction.Tunnel];
    private static readonly object Gate = new();

    // Папка сайтов — внутри папки правил программ: конфигурация ядра знает только её.
    public static string Folder => NextTo(ProgramRuleSets.Folder);
    public static string NextTo(string programs) => Path.Combine(programs, "sites");
    public static string Tag(int level, SiteAction action) => "sites-" + level + "-" + action.ToString().ToLowerInvariant();

    // Все файлы есть всегда, пустые — с заглушкой: ядро не запускается со ссылкой на отсутствующий файл.
    public static void Write(string folder, IReadOnlyList<SiteRule> rules)
    {
        RoutingPlan.Validate(rules);
        var placed = Place(rules);
        lock (Gate)
        {
            Directory.CreateDirectory(folder);
            for (var level = 0; level < Levels; level++)
                foreach (var action in Actions)
                {
                    // Домены — одним правилом; подсети и порты — каждым своим (SiteTarget): правила файла
                    // объединяются через «или».
                    var here = placed.Where(p => p.Level == level && p.Rule.Action == action).Select(p => p.Rule.Domain).ToArray();
                    var domains = here.Where(t => SiteTarget.Kind(t) == SiteTargetKind.Domain).Select(d => (JsonNode)JsonValue.Create(d)!).ToArray();
                    var matches = new JsonArray(new JsonObject { ["domain_suffix"] = domains.Length > 0 ? new JsonArray(domains) : new JsonArray("oblachka-none.invalid") });
                    foreach (var other in here.Where(t => SiteTarget.Kind(t) != SiteTargetKind.Domain)) matches.Add(SiteTarget.SingBox(other));
                    var json = new JsonObject { ["version"] = 2, ["rules"] = matches }.ToJsonString();
                    var path = Path.Combine(folder, Tag(level, action) + ".json");
                    // Без лишней записи: ядро перечитывает файл по изменению.
                    if (File.Exists(path) && File.ReadAllText(path) == json) continue;
                    File.WriteAllText(path + ".tmp", json);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
        }
    }

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

    // Уровень правила: на один выше самого высокого из правил, которые оно обязано опередить
    // (более глубокий сайт — раньше накрывающего). Слишком глубокая вложенность — на последний уровень.
    internal static IReadOnlyList<(SiteRule Rule, int Level)> Place(IReadOnlyList<SiteRule> rules)
    {
        var ordered = rules.OrderByDescending(r => r.Domain.Count(c => c == '.')).ToArray();
        var levels = new int[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var level = 0;
            for (var j = 0; j < i; j++)
                if (ordered[j].Domain.EndsWith("." + ordered[i].Domain, StringComparison.Ordinal)) level = Math.Max(level, levels[j] + 1);
            levels[i] = Math.Min(level, Levels - 1);
        }
        return ordered.Select((r, i) => (r, levels[i])).ToArray();
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

// Правила сайтов и программ файлом («Правила» → «Экспорт» / «Импорт»): перенести на другой
// компьютер, поделиться набором с коллегой. Только правила — без ключей и серверов. При чтении
// каждое правило проверяется так же строго, как вписанное руками; чужой файл не может прислать
// ничего, что нельзя было бы ввести самому.
public static class RulesFile
{
    public const int Version = 1, MaxBytes = 256 * 1024;

    public static string Export(IEnumerable<SiteRule> sites, IEnumerable<ProgramRule> programs) => new JsonObject
    {
        ["oblachka-rules"] = Version,
        ["sites"] = new JsonArray(sites.Select(r => (JsonNode)new JsonObject { ["target"] = r.Domain, ["action"] = Name(r.Action) }).ToArray()),
        ["programs"] = new JsonArray(programs.Select(r => (JsonNode)new JsonObject
        {
            ["executable"] = r.Executable, ["folder"] = r.Folder, ["allInFolder"] = r.AllInFolder, ["site"] = r.Domain, ["action"] = Name(r.Action)
        }).ToArray())
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static (SiteRule[] Sites, ProgramRule[] Programs) Import(string text)
    {
        if (text.Length > MaxBytes) throw new UserError("Файл правил больше 256 КБ.");
        JsonObject root;
        try { root = JsonNode.Parse(text) as JsonObject ?? throw new UserError("Это не файл правил Облачков."); }
        catch (JsonException) { throw new UserError("Это не файл правил Облачков: JSON не разобран."); }
        if (root["oblachka-rules"]?.GetValue<int>() is not Version) throw new UserError("Это не файл правил Облачков или он от более новой версии.");
        try
        {
            var sites = (root["sites"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(s => RoutingPlan.Create((string?)s["target"] ?? "", Action((string?)s["action"]))).DistinctBy(r => r.Domain).ToArray();
            var programs = (root["programs"] as JsonArray ?? []).OfType<JsonObject>().Select(p =>
            {
                var executable = (string?)p["executable"] ?? ""; var folder = (string?)p["folder"]; var action = Action((string?)p["action"]);
                var rule = folder is null ? ProgramRoutingPlan.Create(executable, action)
                    : ProgramRoutingPlan.Create(Path.Combine(folder, executable), action, allInFolder: p["allInFolder"]?.GetValue<bool>() == true);
                return (string?)p["site"] is { } site ? rule with { Domain = RoutingPlan.Create(site, action).Domain } : rule;
            }).DistinctBy(r => r.Key).ToArray();
            RoutingPlan.Validate(sites);
            ProgramRoutingPlan.Validate(programs);
            return (sites, programs);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { throw new UserError("В файле правил повреждена запись."); }
    }

    private static string Name(SiteAction action) => action switch { SiteAction.Direct => "direct", SiteAction.Block => "block", _ => "vpn" };
    private static SiteAction Action(string? name) => name switch
    {
        "direct" => SiteAction.Direct, "block" => SiteAction.Block, "vpn" => SiteAction.Tunnel,
        _ => throw new UserError("В файле правил неизвестное действие: " + name + ".")
    };
}

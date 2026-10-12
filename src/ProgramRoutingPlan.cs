using System.Text.Json.Nodes;

namespace Oblachka;

public sealed record ProgramRule(string Executable, SiteAction Action)
{
    public override string ToString() => Executable + " — " + new SiteRule("", Action).Label;
}

public static class ProgramRoutingPlan
{
    public static ProgramRule Create(string executable, SiteAction action)
    {
        executable = executable.Trim();
        if (!Enum.IsDefined(action) || executable.Length is < 5 or > 120 || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains("..") || executable.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ')))
            throw new UserError("Укажите имя программы, например steam.exe, без пути к файлу.");
        return new(executable, action);
    }
    public static void Validate(IReadOnlyList<ProgramRule>? rules)
    {
        if (rules is null || rules.Count > 100) throw new UserError("Допускается до 100 правил программ.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
            if (rule is null || Create(rule.Executable, rule.Action).Executable != rule.Executable || !seen.Add(rule.Executable))
                throw new UserError("Правила программ повреждены или повторяются.");
    }
    public static JsonObject Compile(NetworkLeaseRequest request, IReadOnlyList<ProgramRule> rules)
    {
        Validate(rules);
        var config = SingBoxTunConfig.Create(request);
        if (rules.Any(r => r.Action == SiteAction.Direct)) ((JsonArray)config["outbounds"]!).Add(new JsonObject { ["type"] = "direct", ["tag"] = "user-direct" });
        foreach (var rule in rules)
        {
            var node = new JsonObject { ["process_name"] = new JsonArray(rule.Executable) };
            if (rule.Action == SiteAction.Block) node["action"] = "reject";
            else node["outbound"] = rule.Action == SiteAction.Direct ? "user-direct" : "local-core";
            ((JsonArray)config["route"]!["rules"]!).Add(node);
        }
        return config;
    }
}

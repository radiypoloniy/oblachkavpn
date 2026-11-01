using System.Text.Json.Nodes;

namespace Oblachka;

internal static class CoreProgramsConfig
{
    internal static void Apply(JsonObject config, CoreStatsEntry? stats)
    {
        if (stats is null) return;
        config["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = "127.0.0.1:" + stats.Port, ["secret"] = stats.Secret } };
        if (stats.ProgramsPort != 0) config["services"] = new JsonArray(new JsonObject
        {
            ["type"] = "oblachka-programs", ["tag"] = "program-statistics", ["listen"] = "127.0.0.1:" + stats.ProgramsPort, ["secret"] = stats.Secret
        });
    }
}

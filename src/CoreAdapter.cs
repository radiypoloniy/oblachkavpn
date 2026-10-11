using System.Text.Json.Nodes;

namespace RoutePilot;

public enum CoreKind { Xray, SingBox }

// Only core-specific syntax lives here. Engine owns the shared process lifecycle.
public sealed record CoreAdapter(CoreKind Kind, string Executable)
{
    public string Label => Kind == CoreKind.Xray ? "Xray 26.9.9" : "sing-box 1.14.3";
    public string[] ValidationArguments => Kind == CoreKind.Xray
        ? ["run", "-test", "-c", "stdin:", "-format", "json"] : ["check", "-c", "stdin"];
    public string[] RunArguments => Kind == CoreKind.Xray
        ? ["run", "-c", "stdin:", "-format", "json"] : ["run", "-c", "stdin"];

    public JsonObject RuntimeConfig(JsonObject outbound, int port)
    {
        if (Kind == CoreKind.Xray) return Profiles.RuntimeConfig(outbound, port);
        var copy = (JsonObject)outbound.DeepClone();
        copy["tag"] = "tunnel";
        return new JsonObject
        {
            ["log"] = new JsonObject { ["disabled"] = true },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "socks", ["tag"] = "local", ["listen"] = "127.0.0.1",
                ["listen_port"] = port
            }),
            ["outbounds"] = new JsonArray(copy),
            ["route"] = new JsonObject
            {
                ["final"] = "tunnel",
                ["rules"] = new JsonArray(new JsonObject { ["network"] = "udp", ["action"] = "reject" })
            }
        };
    }
}

using System.Text.Json.Nodes;

namespace Oblachka;

public enum CoreKind { Xray, SingBox }

// Only core-specific syntax lives here. Engine owns the shared process lifecycle.
public sealed record CoreAdapter(CoreKind Kind, string Executable)
{
    public string Label => Kind == CoreKind.Xray ? "Xray 26.10.21" : "sing-box 1.14.3";
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
                // mixed = SOCKS5 and HTTP on one port; HTTP is needed for the Windows system proxy.
                ["type"] = "mixed", ["tag"] = "local", ["listen"] = "127.0.0.1",
                ["listen_port"] = port
            }),
            // WireGuard в sing-box — endpoint, а не outbound; метка «tunnel» у него та же.
            ["outbounds"] = ProfileServer.IsWireGuard(copy) ? new JsonArray() : new JsonArray(copy),
            ["endpoints"] = ProfileServer.IsWireGuard(copy) ? new JsonArray(copy) : new JsonArray(),
            // Весь трафик уходит в единственный outbound, включая UDP: Hysteria2 переносит
            // его штатно, а запрет оставался от первой осторожной конфигурации.
            // Массив правил остаётся пустым, а не исчезает: в него дописывают планы
            // маршрутизации по сайтам и программам.
            ["route"] = new JsonObject { ["final"] = "tunnel", ["rules"] = new JsonArray() }
        };
    }
}

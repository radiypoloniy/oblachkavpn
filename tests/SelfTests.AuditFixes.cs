using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // Исправления по аудиту 19.10, которые не легли в проверки своих разделов.
    private static async Task AuditFixChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Window core Xray profile keeps only reviewed top-level fields (audit V-4)", () => Sync(() =>
        {
            var outbound = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@example.com:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sni=example.com&fp=chrome&type=tcp&encryption=none#R").Outbound;
            outbound["futureFeature"] = new JsonObject { ["script"] = "x" };
            var stream = outbound["streamSettings"]!.AsObject();
            stream["finalmask"] = new JsonObject { ["lua"] = "os.exit()" };
            stream["sockopt"] = new JsonObject { ["interface"] = "Ethernet", ["mark"] = 1, ["tcpFastOpen"] = true };
            var profile = Profiles.FromOutbound(outbound, "R");
            var kept = profile.Outbound["streamSettings"]!.AsObject();
            Assert(profile.Outbound["futureFeature"] is null && kept["finalmask"] is null && kept["realitySettings"] is not null);
            Assert(kept["sockopt"]!["interface"] is null && kept["sockopt"]!["mark"] is null && (bool?)kept["sockopt"]!["tcpFastOpen"] == true);
        }));
    }
}

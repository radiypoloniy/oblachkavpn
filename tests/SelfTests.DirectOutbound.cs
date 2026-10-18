using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private const string realityLink = "vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=reality&sni=www.example.com&fp=chrome&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&sid=6ba85179e30d4fc2&flow=xtls-rprx-vision&type=tcp#R";

    private static async Task DirectOutboundChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var peers = new[] { IPAddress.Parse("203.0.113.7") };
        await check("Xray VLESS REALITY Vision, Trojan ws and Shadowsocks 2022 translate to sing-box outbounds the service accepts unchanged", async () =>
        {
            var reality = DirectOutbound.From(Profiles.Parse(realityLink))!;
            Assert(reality["type"]!.GetValue<string>() == "vless" && reality["flow"]!.GetValue<string>() == "xtls-rprx-vision" && reality["packet_encoding"]!.GetValue<string>() == "xudp");
            Assert(reality["tls"]!["reality"]!["public_key"]!.GetValue<string>() == "SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc" && reality["tls"]!["utls"]!["fingerprint"]!.GetValue<string>() == "chrome");
            var trojan = DirectOutbound.From(Profiles.Parse("trojan://fixture-secret@203.0.113.7:8443?type=ws&path=%2Fws&host=cdn.example.com&sni=example.com#T"))!;
            Assert(trojan["transport"]!["type"]!.GetValue<string>() == "ws" && trojan["transport"]!["headers"]!["Host"]!.GetValue<string>() == "cdn.example.com");
            var ss = DirectOutbound.From(Profiles.Parse("ss://" + B64("2022-blake3-aes-128-gcm:ISQz02o+aQdntLC3EX4AjQ==").TrimEnd('=') + "@203.0.113.7:443#S"))!;
            Assert(ss["method"]!.GetValue<string>() == "2022-blake3-aes-128-gcm");
            foreach (var outbound in new[] { reality, trojan, ss })
            {
                var accepted = DirectOutboundPolicy.Validate(outbound, peers);
                Assert(JsonNode.DeepEquals(accepted, outbound));
                // Закреплённая версия sing-box принимает выход в рабочей конфигурации.
                var config = new JsonObject { ["log"] = new JsonObject { ["disabled"] = true }, ["outbounds"] = new JsonArray(accepted.DeepClone()) };
                await CoreConfigurationCheck.ValidateAsync(new(CoreKind.SingBox, Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe")), config, CancellationToken.None);
            }
        });
        await check("Profiles sing-box cannot carry the same way stay on the window core", () => Sync(() =>
        {
            Assert(DirectOutbound.From(Profiles.Parse("vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=reality&sni=www.example.com&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&type=xhttp&path=%2Fx#X")) is null);
            Assert(DirectOutbound.From(Profiles.Parse("vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=tls&sni=example.com&type=ws&path=%2Fws%3Fed%3D2048#E")) is null);
            Assert(DirectOutbound.From(Profiles.Parse("vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=tls&sni=example.com&ech=AEX%2B#ECH")) is null);
            var mux = Profiles.Parse(realityLink); mux.Outbound["mux"] = new JsonObject { ["enabled"] = true };
            Assert(DirectOutbound.From(mux) is null);
            var sockopt = Profiles.Parse(realityLink); sockopt.Outbound["streamSettings"]!["sockopt"] = new JsonObject { ["dialerProxy"] = "x" };
            Assert(DirectOutbound.From(sockopt) is null);
            // Адрес не IP — профиль ещё не закреплён планом узла.
            Assert(DirectOutbound.From(Profiles.Parse(realityLink.Replace("203.0.113.7", "node.example.com"))) is null);
        }));
        await check("Service rebuilds direct outbounds and refuses foreign servers, extra fields and disabled certificate checks", () => Sync(() =>
        {
            var good = DirectOutbound.From(Profiles.Parse(realityLink))!;
            JsonObject With(Action<JsonObject> change) { var copy = (JsonObject)good.DeepClone(); change(copy); return copy; }
            ExpectUserError(() => DirectOutboundPolicy.Validate(good, new[] { IPAddress.Parse("198.51.100.1") }));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["detour"] = "direct"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["bind_interface"] = "Ethernet"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["type"] = "direct"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["tls"]!["insecure"] = true), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["tls"]!["certificate_path"] = "C:\\x.pem"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["tls"]!.AsObject().Remove("utls")), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["server"] = "localhost"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["flow"] = "xtls-rprx-direct"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["transport"] = new JsonObject { ["type"] = "ws", ["path"] = "/a b" }), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["tls"]!["enabled"] = "true"), peers));
        }));
    }
}

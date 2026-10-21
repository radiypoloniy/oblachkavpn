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
        await check("Live tree: sites with speed per program, switching shows at once", () => Sync(() =>
        {
            const string json = """
                {"connections":[
                 {"id":"a","upload":1000,"download":9000,"metadata":{"network":"tcp","destinationIP":"142.250.1.1","destinationPort":"443","host":"youtube.com","processPath":"C:\\Apps\\Chrome\\chrome.exe"},"chains":["local-core"]},
                 {"id":"b","upload":10,"download":10,"metadata":{"network":"tcp","destinationIP":"142.250.1.2","destinationPort":"443","host":"youtube.com","processPath":"C:\\Apps\\Chrome\\chrome.exe"},"chains":["user-direct"]},
                 {"id":"c","upload":0,"download":0,"metadata":{"network":"tcp","destinationIP":"8.8.4.4","destinationPort":"853","processPath":"C:\\Apps\\Chrome\\chrome.exe"},"chains":["local-core"]}
                ]}
                """;
            var first = CoreConnections.Parse(json);
            Assert(first[0] is { Upload: 1000, Download: 9000, Direct: false } && first[1].Direct);
            var speed = new LiveSpeed();
            var start = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            // Первый опрос — без скорости: всё, что соединение передало раньше, не всплеск.
            Assert(speed.Measure(first, start).Count == 0);
            var later = first.Select(c => c.Id == "a" ? c with { Download = 9000 + 2 * 1048576 } : c).ToArray();
            var rates = speed.Measure(later, start.AddSeconds(2));
            Assert(rates["a"] == 1048576 && rates["b"] == 0);
            var chrome = LiveApps.FromCore(later, _ => false, rates).Single();
            var youtube = chrome.Sites.First();
            Assert(chrome.Speed == 1048576 && youtube is { Name: "youtube.com", Vpn: 1, Direct: 1 } && youtube.Ids.Length == 2);
            Assert(chrome.Sites.Any(s => s.Name == "8.8.4.4") && Views.LiveRouteRow.Speed(1048576 + 104858) == (1.1).ToString("0.0") + " МБ/с");
            // Нажали «VPN» — плашка сразу «→ через VPN…», пока соединения не переедут.
            var row = new Views.SiteRow("youtube.com");
            row.ShowRoute(1, 1);
            Assert(row.RouteKind == "mix");
            row.SwitchTo(SiteAction.Tunnel);
            Assert(row.RouteKind == "switch" && row.Route.StartsWith("→"));
            row.ShowRoute(1, 1);
            Assert(row.RouteKind == "switch");
            row.ShowRoute(2, 0);
            Assert(row.RouteKind == "vpn");
        }));
    }
}

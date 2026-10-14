using System.Text;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ForeignClientChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Foreign clients: subscriptions are found in Happ, NekoBox, v2RayTun, FlClashX and Clash Verge files", () => Sync(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "oblachka-foreign-" + Guid.NewGuid().ToString("N"));
            try
            {
                var roots = new ForeignClients.Roots(Path.Combine(root, "Roaming"), Path.Combine(root, "Local"), Path.Combine(root, "User"), Path.Combine(root, "Programs"));
                void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
                Write(Path.Combine(roots.LocalAppData, "Happ", "logs", "subscription_log.txt"),
                    "[05.10.2026] Subscription #1 starting update from: https://happ.invalid/sub/old\n" +
                    "[06.10.2026] Subscription #1 starting update from: https://happ.invalid/sub/new\n");
                var group = Path.Combine(roots.LocalAppData, "nekobox", "settings", "groups", "1.cfg");
                Directory.CreateDirectory(Path.GetDirectoryName(group)!);
                File.WriteAllBytes(group, new byte[] { 0, 0, 0, 7 }.Concat(Encoding.BigEndianUnicode.GetBytes("https://neko.invalid/s/abc")).Concat(new byte[] { 0, 0, 0xFF }).ToArray());
                var sub = new JsonObject { ["remarks"] = "Liberty", ["url"] = "https://tun.invalid/subs/1" }.ToJsonString();
                Write(Path.Combine(roots.AppData, "v2RayTun.net", "v2RayTun", "shared_preferences.json"),
                    new JsonObject { ["flutter.sub_1"] = sub, ["flutter.sub_2"] = new JsonObject { ["url"] = "http://plain.invalid/x" }.ToJsonString(), ["flutter.other"] = 5 }.ToJsonString());
                var config = new JsonObject { ["profiles"] = new JsonArray(new JsonObject { ["label"] = "tests", ["url"] = "https://clashx.invalid/p" }) }.ToJsonString();
                Write(Path.Combine(roots.AppData, "com.follow", "clashx", "shared_preferences.json"), new JsonObject { ["flutter.config"] = config }.ToJsonString());
                Write(Path.Combine(roots.AppData, "io.github.clash-verge-rev.clash-verge-rev", "profiles.yaml"),
                    "current: a\nitems:\n- uid: a\n  type: remote\n  name: \"Verge\"\n  url: https://verge.invalid/c?flag=1\n- uid: b\n  type: local\n  name: Local\n");
                // Повтор адреса в другом клиенте не дублирует строку.
                Write(Path.Combine(roots.AppData, "com.follow", "clash", "shared_preferences.json"),
                    new JsonObject { ["flutter.config"] = new JsonObject { ["profiles"] = new JsonArray(new JsonObject { ["url"] = "https://happ.invalid/sub/new" }) }.ToJsonString() }.ToJsonString());
                // Испорченный файл одного клиента не мешает остальным.
                Write(Path.Combine(roots.AppData, "Throne", "config", "throne.db"), "not a database");

                var found = ForeignClients.Find(roots);
                string Url(string client) => found.Single(f => f.Client == client).Url;
                Assert(found.Count == 5);
                Assert(Url("Happ") == "https://happ.invalid/sub/new");
                Assert(Url("NekoBox") == "https://neko.invalid/s/abc");
                Assert(Url("v2RayTun") == "https://tun.invalid/subs/1" && found.Single(f => f.Client == "v2RayTun").Name == "Liberty");
                Assert(Url("FlClashX") == "https://clashx.invalid/p");
                Assert(Url("Clash Verge") == "https://verge.invalid/c?flag=1" && found.Single(f => f.Client == "Clash Verge").Name == "Verge");
                Assert(found.Single(f => f.Client == "Happ").Name == "happ.invalid");
            }
            finally { Directory.Delete(root, true); }
        }));
        await check("Server ping: endpoints from Xray and sing-box VLESS, Hysteria2 skipped, results per profile", async () =>
        {
            var xray = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@203.0.113.7:8443?security=reality&sni=example.org&pbk=fixturekey#X");
            Assert(ServerPing.Endpoint(xray) == ("203.0.113.7", 8443));
            var hy2 = Profiles.Parse(hy2Link);
            Assert(ServerPing.Endpoint(hy2) is null);
            var results = new Dictionary<string, long?>();
            await ServerPing.PingAllAsync(new[] { xray, hy2 }, (p, ms) => { lock (results) results[p.Name] = ms; }, CancellationToken.None,
                (host, port, _) => Task.FromResult<long?>(host == "203.0.113.7" && port == 8443 ? 42 : null));
            Assert(results.Count == 1 && results[xray.Name] == 42);
        });
    }
}

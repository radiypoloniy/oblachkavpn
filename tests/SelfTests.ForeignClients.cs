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
                static string Tun(string name, string? subscription)
                {
                    var link = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@203.0.113.9:443?security=reality&sni=example.org&pbk=fixturekey#n");
                    var config = new JsonObject { ["remarks"] = name, ["xrayOutbound"] = link.Outbound.DeepClone() };
                    if (subscription is not null) config["subscriptionId"] = subscription;
                    return config.ToJsonString();
                }
                void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
                Write(Path.Combine(roots.LocalAppData, "Happ", "logs", "subscription_log.txt"),
                    "[05.10.2026] Subscription #1 starting update from: https://happ.invalid/sub/old\n" +
                    "[06.10.2026] Subscription #1 starting update from: https://happ.invalid/sub/new\n");
                var group = Path.Combine(roots.LocalAppData, "nekobox", "settings", "groups", "1.cfg");
                Directory.CreateDirectory(Path.GetDirectoryName(group)!);
                File.WriteAllBytes(group, new byte[] { 0, 0, 0, 7 }.Concat(Encoding.BigEndianUnicode.GetBytes("https://neko.invalid/s/abc")).Concat(new byte[] { 0, 0, 0xFF }).ToArray());
                var sub = new JsonObject { ["remarks"] = "Основная", ["url"] = "https://tun.invalid/subs/1" }.ToJsonString();
                Write(Path.Combine(roots.AppData, "v2RayTun.net", "v2RayTun", "shared_preferences.json"),
                    new JsonObject { ["flutter.sub_1"] = sub, ["flutter.sub_2"] = new JsonObject { ["url"] = "http://plain.invalid/x" }.ToJsonString(), ["flutter.other"] = 5,
                        // Отдельные: своё (Xray-вид), повтор, сломанное и узел подписки — в счёт идёт одно.
                        ["flutter.config_1"] = Tun("Мой", ""), ["flutter.config_2"] = Tun("Повтор", null), ["flutter.config_3"] = new JsonObject { ["remarks"] = "x", ["xrayOutbound"] = new JsonObject { ["protocol"] = "vless" } }.ToJsonString(),
                        ["flutter.config_4"] = Tun("Из подписки", "sub1") }.ToJsonString());
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
                string Url(string client) => found.Single(f => f.Client == client && !f.IsLocal).Url;
                Assert(found.Count(f => !f.IsLocal) == 5);
                var local = found.Single(f => f.IsLocal);
                Assert(local.Client == "v2RayTun" && local.Url == "" && local.Profiles.Count == 1 && local.Profiles[0].Name == "Мой");
                Assert(local.Title == "v2RayTun · 1 отдельное подключение" && local.Hint == "добавятся во «Вручную»");
                Assert(Url("Happ") == "https://happ.invalid/sub/new");
                Assert(Url("NekoBox") == "https://neko.invalid/s/abc");
                Assert(Url("v2RayTun") == "https://tun.invalid/subs/1" && found.Single(f => f.Client == "v2RayTun" && !f.IsLocal).Name == "Основная");
                Assert(Url("FlClashX") == "https://clashx.invalid/p");
                Assert(Url("Clash Verge") == "https://verge.invalid/c?flag=1" && found.Single(f => f.Client == "Clash Verge").Name == "Verge");
                Assert(found.Single(f => f.Client == "Happ").Name == "happ.invalid");
            }
            finally { Directory.Delete(root, true); }
        }));
        await check("Installed apps: names lose versions, search understands translit and the wrong layout, own files are hidden", () => Sync(() =>
        {
            Assert(InstalledApps.CleanName("WinSCP 6.5.5") == "WinSCP");
            Assert(InstalledApps.CleanName("Inno Setup, версия 6.7.3") == "Inno Setup");
            Assert(InstalledApps.CleanName("Python 3.14.3 (64-bit)") == "Python");
            Assert(InstalledApps.CleanName("IDLE (Python 3.14 64-bit)") == "IDLE");
            Assert(InstalledApps.CleanName("7-Zip File Manager") == "7-Zip File Manager");
            Assert(InstalledApps.Translit("тел") == "tel" && InstalledApps.SwapLayout("еуд") == "tel" && InstalledApps.SwapLayout("tel") == "еуд");
            var apps = new[] { new InstalledApp("Telegram", "Telegram.exe", "", false), new InstalledApp("Steam", "steam.exe", "", false), new InstalledApp("Яндекс Музыка", "music.exe", "", false) };
            Assert(InstalledApps.Search(apps, "тел").Single().Name == "Telegram");
            Assert(InstalledApps.Search(apps, "ыеуф").Single().Name == "Steam");
            Assert(InstalledApps.Search(apps, "музыка").Single().Name == "Яндекс Музыка");
            Assert(InstalledApps.Search(apps, "").Count == 3);
            var mixed = new[] { new InstalledApp("NekoBox", "nekobox.exe", "", false), new InstalledApp("Telegram", "Telegram.exe", "", false) };
            Assert(InstalledApps.Search(mixed, "т")[0].Name == "Telegram");
            Assert(!InstalledApps.Acceptable("xray.exe", @"C:\Program Files\Oblachka\tools\xray.exe"));
            Assert(!InstalledApps.Acceptable("unins000.exe", @"C:\Program Files\App\unins000.exe"));
            Assert(!InstalledApps.Acceptable("python-3.14.exe", @"C:\ProgramData\Package Cache\{x}\python-3.14.exe"));
            Assert(InstalledApps.Acceptable("Telegram.exe", @"C:\Users\u\AppData\Roaming\Telegram Desktop\Telegram.exe"));
            Assert(ProgramRoutingPlan.Create("Яндекс Музыка.exe", SiteAction.Direct).Executable == "Яндекс Музыка.exe");
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
            // Запрет на этом компьютере — не «нет ответа»: результат не сообщается.
            results.Clear();
            await ServerPing.PingAllAsync(new[] { xray }, (p, ms) => { lock (results) results[p.Name] = ms; }, CancellationToken.None,
                (_, _, _) => Task.FromResult<long?>(ServerPing.Blocked));
            Assert(results.Count == 0);
        });
    }
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // Правила программ по папкам: что принимается, как попадает в ядро и через службу.
    private static async Task ProgramScopeChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Whole-folder program rules refuse drives, Windows, Program Files and the user profile", () => Sync(() =>
        {
            foreach (var broad in new[] { @"C:\", @"C:\Windows\System32", @"C:\Program Files", @"D:\Program Files (x86)", @"C:\Program Files\Common Files",
                @"C:\Users\Bob", @"C:\Users\Bob\Downloads", @"C:\Users\Bob\AppData\Local", @"C:\Users\Bob\AppData\Local\Programs", @"D:\Games", @"C:\ProgramData" })
                ExpectUserError(() => ProgramRoutingPlan.Create(broad.TrimEnd('\\') + @"\app.exe", SiteAction.Direct, allInFolder: true));
            foreach (var bad in new[] { @"relative\app.exe", @"C:\a:b\app.exe", @"C:\a\..\b\app.exe", @"\\server\share\app.exe", @"C:\a*\app.exe" })
                ExpectUserError(() => ProgramRoutingPlan.Create(bad, SiteAction.Direct, allInFolder: false));
            Assert(ProgramRoutingPlan.Create(@"c:\Program Files (x86)\Steam\steam.exe", SiteAction.Direct, true).Folder == @"C:\Program Files (x86)\Steam");
            Assert(ProgramRoutingPlan.Create(@"C:\Users\Bob\AppData\Local\Discord\Update.exe", SiteAction.Tunnel, true).AllInFolder);
            // Один файл можно указать и в широкой папке — он точный.
            Assert(ProgramRoutingPlan.Create(@"C:\Users\Bob\Downloads\tool.exe", SiteAction.Direct, false).FilePath == @"C:\Users\Bob\Downloads\tool.exe");
            ExpectUserError(() => ProgramRoutingPlan.Validate([new ProgramRule("x.exe", SiteAction.Direct) { AllInFolder = true }]));
            ExpectUserError(() => ProgramRoutingPlan.Validate([new ProgramRule("x.exe", SiteAction.Direct) { Folder = @"C:\Windows", AllInFolder = true }]));
        }));
        await check("Folder rules reach sing-box as escaped path patterns, most specific first, and sing-box accepts them", async () =>
        {
            ProgramRule[] rules =
            [
                ProgramRoutingPlan.Create(@"C:\Program Files (x86)\Steam\steam.exe", SiteAction.Direct, true),
                ProgramRoutingPlan.Create(@"C:\Program Files (x86)\Steam\steamapps\common\Aion 2\Aion2.exe", SiteAction.Tunnel, true),
                ProgramRoutingPlan.Create("curl.exe", SiteAction.Block),
                ProgramRoutingPlan.Create(@"D:\Tools [x]\a+b.exe", SiteAction.Direct, false),
            ];
            var lease = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"], Programs: rules);
            await CoreConfigurationCheck.ValidateTunAsync(lease, CancellationToken.None);
            var routes = ((JsonArray)SingBoxTunConfig.Create(lease)["route"]!["rules"]!).Skip(1).Select(r => r!.AsObject()).ToArray();
            string Pattern(int i) => routes[i]["process_path_regex"]![0]!.GetValue<string>();
            Assert(Pattern(0) == @"(?i)^D:\\Tools \[x\]\\a\+b\.exe$" && routes[1]["process_name"]![0]!.GetValue<string>() == "curl.exe");
            Assert(Pattern(2) == @"(?i)^C:\\Program Files \(x86\)\\Steam\\steamapps\\common\\Aion 2\\" && routes[2]["outbound"]!.GetValue<string>() == "local-core");
            Assert(Pattern(3) == @"(?i)^C:\\Program Files \(x86\)\\Steam\\" && routes[3]["outbound"]!.GetValue<string>() == SingBoxTunConfig.UserDirect);
            Assert(SingBoxTunConfig.Create(lease)["route"]!["find_process"]!.GetValue<bool>());
        });
        await check("Folder rules survive the service protocol and old name-only rules still parse", () => Sync(() =>
        {
            var rule = ProgramRoutingPlan.Create(@"C:\Program Files (x86)\Steam\steam.exe", SiteAction.Direct, true);
            var lease = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"], Programs: [rule, ProgramRoutingPlan.Create("game.exe", SiteAction.Block)]);
            var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            var wire = HelperProtocol.Parse<HelperRequest>(JsonSerializer.SerializeToUtf8Bytes(new HelperRequest(1, Guid.NewGuid(), HelperOperation.Acquire, lease), options));
            var validated = NetworkHelperContracts.Validate(wire.Lease!);
            Assert(validated.ProgramRules[0] == rule && validated.ProgramRules[1].Folder is null);
            var old = JsonSerializer.Deserialize<ProgramRule>("{\"Executable\":\"steam.exe\",\"Action\":\"Direct\"}", options)!;
            Assert(old.Folder is null && !old.AllInFolder && old.Key == "steam.exe");
            // Подменённый путь в правиле служба не принимает.
            var forged = lease with { Programs = [rule with { Folder = @"C:\Windows" }] };
            ExpectUserError(() => NetworkHelperContracts.Validate(forged));
        }));
        await check("Live program rule files keep precedence by levels and reach sing-box without a reconnect", async () =>
        {
            var steam = ProgramRoutingPlan.Create(@"C:\Steam\steam.exe", SiteAction.Direct, true);
            var game = ProgramRoutingPlan.Create(@"C:\Steam\steamapps\common\Game\game.exe", SiteAction.Tunnel, true);
            var name = ProgramRoutingPlan.Create("game.exe", SiteAction.Block);
            var exact = ProgramRoutingPlan.Create(@"C:\Steam\steamapps\common\Game\game.exe", SiteAction.Direct, false);
            var other = ProgramRoutingPlan.Create(@"D:\Apps\Tool\tool.exe", SiteAction.Block, true);
            var levels = ProgramRuleSets.Place([steam, game, name, exact, other]).ToDictionary(p => p.Rule.Key, p => p.Level);
            Assert(levels[exact.Key] == 0 && levels[name.Key] == 1 && levels[game.Key] == 2 && levels[steam.Key] == 3 && levels[other.Key] == 2);
            var folder = Path.Combine(Path.GetTempPath(), "oblachka-programs-" + Guid.NewGuid().ToString("N"));
            try
            {
                ProgramRuleSets.Write(folder, [steam, game]);
                SiteRuleSets.Write(SiteRuleSets.NextTo(folder), []);
                Assert(Directory.GetFiles(folder, "*.json").Length == ProgramRuleSets.Levels * 3);
                var gameSet = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, ProgramRuleSets.Tag(0, SiteAction.Tunnel) + ".json")))!;
                Assert(gameSet["rules"]![0]!["process_path_regex"]![0]!.GetValue<string>().Contains(@"common\\Game\\"));
                // Без «напрямую» при подключении: уровни «напрямую» блокируют, а не выпускают мимо туннеля.
                var strict = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"]);
                await CoreConfigurationCheck.ValidateTunAsync(strict, CancellationToken.None, programSets: folder);
                var config = SingBoxTunConfig.Create(strict, programSets: folder);
                var direct = ((JsonArray)config["route"]!["rules"]!).Single(r => r!["rule_set"]?[0]?.GetValue<string>() == ProgramRuleSets.Tag(1, SiteAction.Direct))!;
                Assert(direct["action"]!.GetValue<string>() == "reject" && config["route"]!["find_process"]!.GetValue<bool>());
                var open = SingBoxTunConfig.Create(strict with { Programs = [steam] }, programSets: folder);
                Assert(((JsonArray)open["route"]!["rules"]!).Single(r => r!["rule_set"]?[0]?.GetValue<string>() == ProgramRuleSets.Tag(1, SiteAction.Direct))!["outbound"]!.GetValue<string>() == SingBoxTunConfig.UserDirect);
            }
            finally { Directory.Delete(folder, true); }
            var request = new HelperRequest(1, Guid.NewGuid(), HelperOperation.ProgramRules, null, Programs: new([steam]));
            HelperProtocol.Validate(request);
            ExpectUserError(() => HelperProtocol.Validate(request with { Operation = HelperOperation.Heartbeat }));
            ExpectUserError(() => HelperProtocol.Validate(request with { Programs = new([steam with { Folder = @"C:\" }]) }));
        });
        await check("Live connections group by program, tell tunnel from direct and find the rule the core applies", () => Sync(() =>
        {
            var tun = System.Net.IPAddress.Parse(SingBoxTunConfig.TunHost4);
            var lan = System.Net.IPAddress.Parse("192.168.1.10");
            var web = System.Net.IPAddress.Parse("203.0.113.5");
            var loop = System.Net.IPAddress.Loopback;
            LiveSocket[] sockets =
            [
                new(10, false, tun, 50001, web, 443, ConnectionTable.Established),
                new(11, false, tun, 50002, web, 443, ConnectionTable.Established),
                new(10, false, lan, 50003, web, 80, ConnectionTable.Established),
                new(20, false, lan, 50004, web, 443, ConnectionTable.Established),
                new(20, false, System.Net.IPAddress.Any, 8080, System.Net.IPAddress.Any, 0, ConnectionTable.Listening),
                new(30, false, loop, 50005, loop, 10808, ConnectionTable.Established),
                new(20, true, lan, 5353, null, 0, 0),
                new(40, false, tun, 50006, web, 443, ConnectionTable.Established),
            ];
            var paths = new Dictionary<int, string?> { [10] = @"C:\Apps\Chrome\chrome.exe", [11] = @"C:\Apps\Chrome\chrome.exe", [20] = @"D:\Games\Game\game.exe", [30] = @"C:\Apps\Tool\tool.exe", [40] = @"C:\Program Files\Oblachka\tools\sing-box.exe" };
            var apps = LiveApps.Group(sockets, true, 0, pid => paths.GetValueOrDefault(pid), p => p.StartsWith(@"C:\Program Files\Oblachka\", StringComparison.OrdinalIgnoreCase));
            Assert(apps.Count == 2 && apps[0] is { Vpn: 2, Direct: 1 } && apps[0].Path.EndsWith("chrome.exe") && apps[1] is { Vpn: 0, Direct: 1, Udp: 1 });
            // Без туннеля через VPN только то, что идёт в прокси Облачки.
            var proxy = LiveApps.Group(sockets, false, 10808, pid => paths.GetValueOrDefault(pid), _ => false);
            Assert(proxy.Single(a => a.Path.EndsWith("tool.exe")).Vpn == 1 && proxy.Single(a => a.Path.EndsWith("chrome.exe")).Direct == 3);
            ProgramRule[] rules = [ProgramRoutingPlan.Create(@"D:\Lib\game.exe", SiteAction.Block, true), ProgramRoutingPlan.Create(@"D:\Lib\Game\game.exe", SiteAction.Tunnel, true), ProgramRoutingPlan.Create("chrome.exe", SiteAction.Direct)];
            Assert(ProgramRoutingPlan.Find(rules, @"D:\Lib\Game\bin\game.exe")!.Action == SiteAction.Tunnel);
            Assert(ProgramRoutingPlan.Find(rules, @"D:\Lib\Other\other.exe")!.Action == SiteAction.Block);
            Assert(ProgramRoutingPlan.Find(rules, @"C:\Apps\Chrome\chrome.exe")!.Action == SiteAction.Direct && ProgramRoutingPlan.Find(rules, @"C:\x\y.exe") is null);
        }));
        await check("Selective VPN: only chosen sites and programs enter the tunnel, the rest goes direct with local DNS", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "oblachka-selective-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var ruleSet = Path.Combine(folder, "direct-dns.json");
                File.WriteAllText(ruleSet, "{\"version\":2,\"rules\":[{\"domain_suffix\":[\"oblachka.invalid\"]}]}");
                var dns = System.Net.IPAddress.Parse("192.168.1.1");
                var lease = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"],
                    [RoutingPlan.Create("youtube.com", SiteAction.Tunnel), RoutingPlan.Create("discord.com", SiteAction.Tunnel)], [ProgramRoutingPlan.Create("Discord.exe", SiteAction.Tunnel)], DirectByDefault: true);
                Assert(NetworkHelperContracts.Validate(lease).HasDirect);
                await CoreConfigurationCheck.ValidateTunAsync(lease, CancellationToken.None, true, dns, ruleSet);
                var config = SingBoxTunConfig.Create(lease, true, dns, ruleSet);
                Assert(config["route"]!["final"]!.GetValue<string>() == SingBoxTunConfig.UserDirect && config["dns"]!["final"]!.GetValue<string>() == DirectDns.ServerTag);
                var first = config["dns"]!["rules"]![0]!;
                Assert(first["server"]!.GetValue<string>() == "remote-dns" && first["domain_suffix"]!.AsArray().Select(d => d!.GetValue<string>()).SequenceEqual(["youtube.com", "discord.com"]));
                var rules = ((JsonArray)config["route"]!["rules"]!).Select(r => r!.AsObject()).ToArray();
                Assert(rules.Any(r => r["process_name"]?[0]?.GetValue<string>() == "Discord.exe" && r["outbound"]!.GetValue<string>() == "local-core"));
                Assert(rules.Any(r => r["domain_suffix"]?[0]?.GetValue<string>() == "youtube.com" && r["outbound"]!.GetValue<string>() == "local-core"));
                // Обычный режим не меняется: остальное — в туннель.
                Assert(SingBoxTunConfig.Create(lease with { DirectByDefault = false }, true, dns, ruleSet)["route"]!["final"]!.GetValue<string>() == "local-core");
                ExpectUserError(() => NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, [], DirectByDefault: true)));
            }
            finally { Directory.Delete(folder, true); }
        });
        await check("Steam libraries, Squirrel apps and the app scope pick whole program folders", () => Sync(() =>
        {
            var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}";
            var paths = GameLibraries.Vdf(vdf).Where(p => p.Key == "path").Select(p => p.Value).ToArray();
            Assert(paths.SequenceEqual([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"]));
            Assert(AppRuleScope.Root(@"C:\Users\Bob\AppData\Local\Discord\app-1.0.9213\Discord.exe") == @"C:\Users\Bob\AppData\Local\Discord");
            var steam = new InstalledApp("Steam", "steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", false) { Folders = [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary\steamapps\common"] };
            var scoped = AppRuleScope.For(steam, SiteAction.Direct);
            Assert(scoped.Length == 2 && scoped.All(r => r.AllInFolder && r.Executable == "steam.exe"));
            // Программа прямо в «Загрузках»: папка слишком общая — правило на один файл.
            var loose = AppRuleScope.For(new InstalledApp("Tool", "tool.exe", @"C:\Users\Bob\Downloads\tool.exe", false), SiteAction.Direct);
            Assert(loose.Single() is { AllInFolder: false, FilePath: @"C:\Users\Bob\Downloads\tool.exe" });
        }));
        await check("Live list takes the real exit of each connection from the tunnel core", () => Task.Run(() =>
        {
            // Формат clash API sing-box: порт строкой, выходы — в chains.
            const string json = """
                {"connections":[
                 {"metadata":{"network":"tcp","destinationIP":"149.154.167.51","destinationPort":"443","host":"","processPath":"C:\\Apps\\Telegram\\Telegram.exe"},"chains":["user-direct"]},
                 {"metadata":{"network":"udp","destinationIP":"149.154.167.52","destinationPort":"3478","processPath":"C:\\Apps\\Telegram\\Telegram.exe"},"chains":["local-core"]},
                 {"metadata":{"network":"tcp","destinationIP":"87.250.250.242","destinationPort":"443","processPath":"C:\\Apps\\Chrome\\chrome.exe"},"chains":["local-core"]},
                 {"metadata":{"network":"tcp","destinationIP":"142.250.1.1","destinationPort":"443","processPath":"C:\\Apps\\Chrome\\chrome.exe"},"chains":["local-core"]},
                 {"metadata":{"network":"tcp","destinationIP":"87.250.250.242","destinationPort":"443","processPath":"C:\\Program Files\\Oblachka\\tools\\xray.exe"},"chains":["user-direct"]},
                 {"metadata":{"network":"tcp","destinationIP":"1.1.1.1","destinationPort":"443"},"chains":["local-core"]}
                ]}
                """;
            var connections = CoreConnections.Parse(json);
            Assert(connections.Count == 6 && connections[0].Port == 443 && connections[1].Udp);
            var apps = LiveApps.FromCore(connections, path => path.StartsWith(@"C:\Program Files\Oblachka\", StringComparison.OrdinalIgnoreCase));
            Assert(apps.Count == 2);
            var telegram = apps.Single(a => a.Path.EndsWith("Telegram.exe"));
            Assert(telegram is { Direct: 1, Vpn: 1, Udp: 1 });
            // Сайт из «Российских сайтов»: ядро окна вернуло его туннелю на прямой вход — это напрямую.
            var chrome = apps.Single(a => a.Path.EndsWith("chrome.exe"));
            Assert(chrome is { Direct: 1, Vpn: 1 });
            Assert(CoreConnections.Parse("{}").Count == 0);
        }));
        await check("Changing a program rule resets only that program's open connections", () => Task.Run(() =>
        {
            var tg = ProgramRoutingPlan.Create(@"C:\Apps\Telegram\Telegram.exe", SiteAction.Tunnel, allInFolder: true);
            var tgDirect = tg with { Action = SiteAction.Direct };
            Assert(ConnectionReset.Changed([tg], [tgDirect], @"C:\Apps\Telegram\Updater.exe"));
            Assert(ConnectionReset.Changed([tg], [], @"C:\Apps\Telegram\Telegram.exe"));
            Assert(!ConnectionReset.Changed([tg], [tg], @"C:\Apps\Telegram\Telegram.exe"));
            Assert(!ConnectionReset.Changed([tg], [tgDirect], @"C:\Apps\Chrome\chrome.exe"));
        }));

        await check("Connections opened before the tunnel are reset, except the tunnel's, the node's, LAN and direct programs", () => Task.Run(() =>
        {
            var peer = IPAddress.Parse("203.0.113.9");
            var tg = ProgramRoutingPlan.Create(@"C:\Apps\Telegram\Telegram.exe", SiteAction.Direct, allInFolder: true);
            var lease = new ValidatedNetworkLease(Guid.NewGuid(), NetworkLeaseAction.Acquire, 1080, [peer], Programs: [tg]);
            LiveSocket To(string local, string remote) => new(1, false, IPAddress.Parse(local), 50000, IPAddress.Parse(remote), 443, ConnectionTable.Established);
            Assert(ConnectionReset.IsStale(lease, To("192.168.1.5", "198.51.100.7"), @"C:\Apps\Chrome\chrome.exe"));
            Assert(ConnectionReset.IsStale(lease, To("192.168.1.5", "198.51.100.7"), null));
            Assert(!ConnectionReset.IsStale(lease, To(SingBoxTunConfig.TunHost4, "198.51.100.7"), null));
            Assert(!ConnectionReset.IsStale(lease, To("192.168.1.5", "203.0.113.9"), null));
            Assert(!ConnectionReset.IsStale(lease, To("192.168.1.5", "192.168.1.1"), null));
            Assert(!ConnectionReset.IsStale(lease, To("192.168.1.5", "198.51.100.7"), @"C:\Apps\Telegram\Telegram.exe"));
            Assert(ConnectionReset.Stale(lease with { DirectByDefault = true }) == 0);
        }));
    }
}

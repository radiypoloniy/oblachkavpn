using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // Правила сайтов на ходу и «сайт только в этой программе» (ProgramRule.Domain, SiteRuleSets).
    private static async Task LiveSiteRuleChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Program-and-site rule goes before the program rule; sites are levelled by depth", () => Sync(() =>
        {
            var chrome = ProgramRoutingPlan.Create(@"C:\Apps\Chrome\chrome.exe", SiteAction.Tunnel, true);
            var youtube = chrome with { Action = SiteAction.Direct, Domain = "youtube.com" };
            var music = chrome with { Action = SiteAction.Block, Domain = "music.youtube.com" };
            ProgramRoutingPlan.Validate([chrome, youtube, music]);
            Assert(youtube.Key != chrome.Key && youtube.TargetKey == chrome.TargetKey);
            ExpectUserError(() => ProgramRoutingPlan.Validate([chrome with { Domain = "not a domain" }]));
            var levels = ProgramRuleSets.Place([chrome, youtube, music]).ToDictionary(p => p.Rule.Key, p => p.Level);
            Assert(levels[music.Key] < levels[youtube.Key] && levels[youtube.Key] < levels[chrome.Key]);
            var match = ProgramRoutingPlan.Match(youtube);
            Assert(match["process_path_regex"] is not null && match["domain_suffix"]![0]!.GetValue<string>() == "youtube.com");
            // Правило с сайтом не решает за всю программу.
            Assert(ProgramRoutingPlan.Find([youtube], @"C:\Apps\Chrome\chrome.exe") is null);
            var sites = SiteRuleSets.Place([RoutingPlan.Create("youtube.com", SiteAction.Direct), RoutingPlan.Create("music.youtube.com", SiteAction.Tunnel), RoutingPlan.Create("vk.com", SiteAction.Block)])
                .ToDictionary(p => p.Rule.Domain, p => p.Level);
            Assert(sites["music.youtube.com"] == 0 && sites["youtube.com"] == 1 && sites["vk.com"] == 0);
            // Какой путь ядро выберет: программа и сайт → программа → сайт.
            var sent = new SentRules([chrome, youtube], [RoutingPlan.Create("youtube.com", SiteAction.Block), RoutingPlan.Create("vk.com", SiteAction.Direct)]);
            Assert(sent.Of(@"C:\Apps\Chrome\chrome.exe", "rr1.youtube.com") == SiteAction.Direct);
            Assert(sent.Of(@"C:\Apps\Chrome\chrome.exe", "vk.com") == SiteAction.Tunnel);
            Assert(sent.Of(@"C:\Apps\Firefox\firefox.exe", "www.youtube.com") == SiteAction.Block);
            Assert(sent.Of(@"C:\Apps\Firefox\firefox.exe", "example.org") is null);
            Assert(LiveApps.MainDomain("rr3---sn-abc.googlevideo.com") == "googlevideo.com" && LiveApps.MainDomain("news.bbc.co.uk") == "bbc.co.uk"
                && LiveApps.MainDomain("142.250.1.1") is null && LiveApps.MainDomain("1.2.3.4:443") is null);
        }));
        await check("Live routing matches destination IP and port in tunnel rule-set order", () => Sync(() =>
        {
            var rules = new SentRules([ProgramRoutingPlan.Create("game.exe", SiteAction.Tunnel)],
            [
                RoutingPlan.Create("example.net", SiteAction.Direct),
                RoutingPlan.Create("video.example.net", SiteAction.Tunnel),
                RoutingPlan.Create("192.0.2.0/24", SiteAction.Direct),
                RoutingPlan.Create("2001:db8::1", SiteAction.Direct),
                RoutingPlan.Create("port:27015-27030", SiteAction.Block),
                RoutingPlan.Create("port:25", SiteAction.Block)
            ]).Compile();
            SiteAction? At(string? host, string? address, int port, string? path = null) => rules.Of(new(path, true, host, address, port, []));

            Assert(At(null, "192.0.2.255", 443) == SiteAction.Direct);
            Assert(At(null, "192.0.3.0", 443) is null);
            Assert(At(null, "2001:db8::1", 443) == SiteAction.Direct);
            Assert(At(null, "2001:db8::2", 443) is null);
            Assert(At(null, null, 25) == SiteAction.Block);
            Assert(At(null, "", 27015) == SiteAction.Block && At(null, null, 27030) == SiteAction.Block);
            Assert(At(null, null, 27014) is null && At(null, null, 27031) is null);
            Assert(At(null, null, 0) is null);
            // На одном уровне блокировка раньше прямого выхода. Вложенный домен раньше родительского.
            Assert(At("example.net", "192.0.2.5", 27015) == SiteAction.Block);
            Assert(At("video.example.net", "192.0.2.5", 443) == SiteAction.Direct);
            Assert(At("video.example.net", "192.0.3.5", 443) == SiteAction.Tunnel);
            Assert(At(null, "192.0.2.5", 27015, @"C:\Games\game.exe") == SiteAction.Tunnel);
        }));
        await check("Live rule changes select matching TCP and UDP connections without a host or process", () => Sync(() =>
        {
            var connections = CoreConnections.Parse("""
                {"connections":[
                  {"id":"ip","metadata":{"network":"tcp","destinationIP":"192.0.2.3","destinationPort":"443"},"chains":["local-core"]},
                  {"id":"port","metadata":{"network":"udp","destinationIP":"2001:db8::2","destinationPort":27015},"chains":["local-core"]},
                  {"id":"direct","metadata":{"network":"tcp","destinationIP":"192.0.2.4","destinationPort":"443"},"chains":["user-direct"]},
                  {"id":"other","metadata":{"network":"udp","destinationIP":"2001:db8::2","destinationPort":"27031"},"chains":["local-core"]}
                ]}
                """);
            var next = new SentRules([], [RoutingPlan.Create("192.0.2.0/24", SiteAction.Direct), RoutingPlan.Create("port:27015-27030", SiteAction.Block)]);
            var changed = CoreConnections.RuleChangeFilter(SentRules.None, next, onlyMismatched: false);
            var mismatched = CoreConnections.RuleChangeFilter(SentRules.None, next, onlyMismatched: true);
            Assert(connections.Where(changed).Select(c => c.Id).SequenceEqual(["ip", "port", "direct"]));
            Assert(connections.Where(mismatched).Select(c => c.Id).SequenceEqual(["ip", "port"]));
        }));
        await check("Live route details separate a core observation from the current rule for the exact endpoint", () => Sync(() =>
        {
            const string path = @"C:\Games\game.exe";
            var game = ProgramRoutingPlan.Create(path, SiteAction.Block, allInFolder: false) with { Domain = "voice.example.net" };
            var policy = new SentRules([game], [RoutingPlan.Create("port:443", SiteAction.Direct)]).Compile();
            CoreConnection[] connections =
            [
                new(path, true, "voice.example.net", "192.0.2.1", 443, [SingBoxTunConfig.ServerSelector], "voice"),
                new(path, false, "updates.example.net", "192.0.2.2", 443, ["local-core"], "updates")
            ];
            var voice = MainWindow.ExplainSiteSample(connections, ["voice"], policy, directAllowed: false);
            Assert(voice.Contains("Ядро наблюдало — выход через VPN") && voice.Contains("По текущим отправленным правилам") &&
                voice.Contains("game.exe и voice.example.net") && !voice.Contains("updates.example.net"));
            var updates = MainWindow.ExplainSiteSample(connections, ["updates"], policy, directAllowed: false);
            Assert(updates.Contains("передано ядру прокси") && updates.Contains("port:443 для всех программ") && updates.Contains("требует переподключения"));
        }));
        await check("Live site rules reach sing-box as rule-set files and through the service channel", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "test-data", "oblachka-programs-" + Guid.NewGuid().ToString("N"));
            try
            {
                var chrome = ProgramRoutingPlan.Create(@"C:\Apps\Chrome\chrome.exe", SiteAction.Tunnel, true);
                var sites = new[] { RoutingPlan.Create("youtube.com", SiteAction.Direct), RoutingPlan.Create("music.youtube.com", SiteAction.Tunnel),
                    RoutingPlan.Create("192.0.2.0/24", SiteAction.Direct), RoutingPlan.Create("port:27015-27030", SiteAction.Block) };
                ProgramRuleSets.Write(folder, [chrome, chrome with { Action = SiteAction.Direct, Domain = "youtube.com" }]);
                SiteRuleSets.Write(SiteRuleSets.NextTo(folder), sites);
                Assert(Directory.GetFiles(SiteRuleSets.NextTo(folder), "*.json").Length == SiteRuleSets.Levels * 3);
                var level1 = JsonNode.Parse(File.ReadAllText(Path.Combine(SiteRuleSets.NextTo(folder), SiteRuleSets.Tag(1, SiteAction.Direct) + ".json")))!;
                Assert(level1["rules"]![0]!["domain_suffix"]![0]!.GetValue<string>() == "youtube.com");
                var request = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"], Sites: sites);
                await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None, programSets: folder);
                var config = SingBoxTunConfig.Create(request, programSets: folder);
                var rules = ((JsonArray)config["route"]!["rules"]!).OfType<JsonObject>().ToArray();
                // Сайты — файлами после правил программ, без вписанных в конфигурацию доменов; имя сайта ядро узнаёт всегда.
                int Index(string tag) => Array.FindIndex(rules, r => r["rule_set"]?[0]?.GetValue<string>() == tag);
                Assert(Index(SiteRuleSets.Tag(0, SiteAction.Tunnel)) > Index(ProgramRuleSets.Tag(ProgramRuleSets.Levels - 1, SiteAction.Tunnel)));
                Assert(Index(SiteRuleSets.Tag(0, SiteAction.Block)) < Index(SiteRuleSets.Tag(0, SiteAction.Direct)));
                Assert(Index(SiteRuleSets.Tag(0, SiteAction.Direct)) < Index(SiteRuleSets.Tag(0, SiteAction.Tunnel)));
                Assert(!rules.Any(r => r["domain_suffix"] is not null) && rules.Any(r => (string?)r["action"] == "sniff"));
            }
            finally { Directory.Delete(folder, true); }
            var helper = new HelperRequest(1, Guid.NewGuid(), HelperOperation.ProgramRules, null, Programs: new([], [RoutingPlan.Create("youtube.com", SiteAction.Direct)]));
            HelperProtocol.Validate(helper);
            ExpectUserError(() => HelperProtocol.Validate(helper with { Programs = new([], [new SiteRule("bad domain", SiteAction.Direct)]) }));
        });
    }
}

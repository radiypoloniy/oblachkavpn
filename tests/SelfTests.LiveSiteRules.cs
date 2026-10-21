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
        await check("Live site rules reach sing-box as rule-set files and through the service channel", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "oblachka-programs-" + Guid.NewGuid().ToString("N"));
            try
            {
                var chrome = ProgramRoutingPlan.Create(@"C:\Apps\Chrome\chrome.exe", SiteAction.Tunnel, true);
                var sites = new[] { RoutingPlan.Create("youtube.com", SiteAction.Direct), RoutingPlan.Create("music.youtube.com", SiteAction.Tunnel) };
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
                Assert(!rules.Any(r => r["domain_suffix"] is not null) && rules.Any(r => (string?)r["action"] == "sniff"));
            }
            finally { Directory.Delete(folder, true); }
            var helper = new HelperRequest(1, Guid.NewGuid(), HelperOperation.ProgramRules, null, Programs: new([], [RoutingPlan.Create("youtube.com", SiteAction.Direct)]));
            HelperProtocol.Validate(helper);
            ExpectUserError(() => HelperProtocol.Validate(helper with { Programs = new([], [new SiteRule("bad domain", SiteAction.Direct)]) }));
        });
    }
}

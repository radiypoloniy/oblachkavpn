using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // «Для продвинутых»: незаполненный раздел даёт ровно прежнюю конфигурацию; заполненный — то,
    // что задано, и ничего мимо защиты сверх заявленного. Служба проверяет значения заново.
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static async Task AdvancedChecksAsync(Func<string, Func<Task>, Task> check)
    {
        NetworkLeaseRequest Lease(TunnelTuning? tuning) => new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["203.0.113.7"], Tuning: tuning);

        await check("Advanced options validate what a person may type and keep defaults as before", () => Sync(() =>
        {
            Assert(AdvancedOptions.Default.IsDefault && AdvancedOptions.Default.Problem() is null && TunnelTuning.From(AdvancedOptions.Default) is null);
            Assert(AdvancedOptions.CheckMtu(1500) is null && AdvancedOptions.CheckMtu(576) is not null && AdvancedOptions.CheckMtu(65000) is not null);
            // Только DoH: DoT и DoQ через встроенный Xray замолкали на стенде.
            Assert(AdvancedOptions.CheckDns("https://dns.adguard-dns.com/dns-query") is null && AdvancedOptions.CheckDns("https://9.9.9.9/dns-query") is null);
            Assert(AdvancedOptions.CheckDns("tls://1.1.1.1") is not null && AdvancedOptions.CheckDns("quic://dns.example") is not null);
            Assert(AdvancedOptions.CheckDns("http://dns.example/dns-query") is not null && AdvancedOptions.CheckDns("8.8.8.8") is not null && AdvancedOptions.CheckDns("https://user:pw@dns.example/") is not null);
            Assert(AdvancedOptions.CheckSubnets(["10.20.0.0/16", "192.168.77.0/24", "fd00:1::/48"]) is null);
            // Шире /8 — почти весь интернет мимо VPN: не принимается.
            Assert(AdvancedOptions.CheckSubnets(["0.0.0.0/0"]) is not null && AdvancedOptions.CheckSubnets(["10.0.0.0/7"]) is not null && AdvancedOptions.CheckSubnets(["10.0.0.0"]) is not null);
            Assert(AdvancedOptions.CheckFragment("tlshello", "50-100", "5-10") is null && AdvancedOptions.CheckFragment("abc", null, null) is not null);
            Assert(AdvancedOptions.CheckUserAgent("Happ/2.0") is null && AdvancedOptions.CheckUserAgent("Облачка") is not null);
            Assert(AdvancedOptions.CheckProbe("https://intranet.example/health") is null && AdvancedOptions.CheckProbe("http://x") is not null);
            Assert(AdvancedOptions.CheckPort(2080) is null && AdvancedOptions.CheckPort(80) is not null);
            // Своя форма фрагментации поверх провайдерской: незаданное — из неё.
            var shape = (AdvancedOptions.Default with { FragmentLength = "10-20" }).Fragment(new ProviderFragment(true, "1-3", "40-60", "2-4"))!;
            Assert(shape is { Packets: "1-3", Length: "10-20", Interval: "2-4", Enabled: true });
            Assert(AdvancedOptions.Default.Fragment(null) is null);
            Assert(TunnelTuning.Normalize("10.20.1.5/16") == "10.20.0.0/16" && TunnelTuning.Normalize("fd00:1:2:3::9/48") == "fd00:1:2::/48");
        }));

        await check("Tunnel tuning changes only the stack, MTU, DNS server and exclusions it names", () => Sync(() =>
        {
            var plain = SingBoxTunConfig.Create(Lease(null), ipv6: false);
            var tuned = SingBoxTunConfig.Create(Lease(new(TunnelStack.System, 1400, "https://dns.example/custom-query", ["10.20.0.0/16"])), ipv6: false);
            JsonObject Tun(JsonObject c) => (JsonObject)((JsonArray)c["inbounds"]!)[0]!;
            Assert((string?)Tun(plain)["stack"] == "gvisor" && (int?)Tun(plain)["mtu"] == 9000);
            Assert((string?)Tun(tuned)["stack"] == "system" && (int?)Tun(tuned)["mtu"] == 1400);
            Assert(Tun(tuned)["route_exclude_address"]!.ToJsonString().Contains("10.20.0.0/16") && !Tun(plain)["route_exclude_address"]!.ToJsonString().Contains("10.20.0.0/16"));
            var servers = (JsonArray)tuned["dns"]!["servers"]!;
            var remote = (JsonObject)servers.First(s => (string?)s!["tag"] == "remote-dns")!;
            Assert((string?)remote["server"] == "dns.example" && (string?)remote["path"] == "/custom-query" && (string?)remote["domain_resolver"] == "dns-bootstrap");
            Assert((string?)remote["tls"]!["server_name"] == "dns.example");
            // Загрузочный — встроенный DoH 1.1.1.1 тем же путём, не UDP.
            Assert(servers.Any(s => (string?)s!["tag"] == "dns-bootstrap" && (string?)s!["type"] == "https" && (string?)s!["server"] == "1.1.1.1" && s!["detour"]!.ToString() == remote["detour"]!.ToString()));
            // DoH по IP — без загрузочного DNS.
            var byIp = SingBoxTunConfig.Create(Lease(new(Dns: "https://9.9.9.9/dns-query")), ipv6: false);
            Assert(((JsonArray)byIp["dns"]!["servers"]!).All(s => (string?)s!["tag"] != "dns-bootstrap"));
        }));

        await check("Site rules take subnets and ports beside domains, in both cores and in live rule files", () => Sync(() =>
        {
            Assert(RoutingPlan.Create("10.20.1.5/16", SiteAction.Direct).Domain == "10.20.0.0/16" && RoutingPlan.Create("8.8.8.8", SiteAction.Block).Domain == "8.8.8.8/32");
            Assert(RoutingPlan.Create("port:25", SiteAction.Block).Domain == "port:25" && RoutingPlan.Create("ПОРТ:27015-27030", SiteAction.Direct).Domain == "port:27015-27030");
            Assert(RoutingPlan.Create("Example.COM", SiteAction.Tunnel).Domain == "example.com");
            foreach (var bad in new[] { "port:0", "port:30-20", "port:70000", "0.0.0.0/0", "10.0.0.0/40", "https://x.com/a" })
                Assert(Throws<UserError>(() => RoutingPlan.Create(bad, SiteAction.Direct)));
            RoutingPlan.Validate([new("10.20.0.0/16", SiteAction.Direct), new("port:25", SiteAction.Block), new("example.com", SiteAction.Tunnel)]);
            Assert(SiteTarget.SingBox("10.20.0.0/16").ToJsonString() == "{\"ip_cidr\":[\"10.20.0.0/16\"]}");
            Assert(SiteTarget.SingBox("port:25").ToJsonString() == "{\"port\":[25]}" && SiteTarget.SingBox("port:27015-27030").ToJsonString() == "{\"port_range\":[\"27015:27030\"]}");
            Assert(SiteTarget.Xray("port:27015-27030")["port"]!.GetValue<string>() == "27015-27030" && SiteTarget.Xray("10.20.0.0/16")["ip"]!.ToJsonString() == "[\"10.20.0.0/16\"]");
            var folder = Path.Combine(Path.GetTempPath(), "obl-sites-" + Guid.NewGuid().ToString("N"));
            try
            {
                SiteRuleSets.Write(folder, [new("10.20.0.0/16", SiteAction.Direct), new("example.com", SiteAction.Direct), new("port:25", SiteAction.Block)]);
                var direct = File.ReadAllText(Path.Combine(folder, SiteRuleSets.Tag(0, SiteAction.Direct) + ".json"));
                var block = File.ReadAllText(Path.Combine(folder, SiteRuleSets.Tag(0, SiteAction.Block) + ".json"));
                Assert(direct.Contains("\"ip_cidr\":[\"10.20.0.0/16\"]") && direct.Contains("example.com") && block.Contains("\"port\":[25]"));
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        }));

        await check("Rules file round-trips sites, subnets, ports and programs, and rejects what cannot be typed", () => Sync(() =>
        {
            SiteRule[] sites = [new("example.com", SiteAction.Direct), new("10.20.0.0/16", SiteAction.Direct), new("port:25", SiteAction.Block)];
            ProgramRule[] programs = [ProgramRoutingPlan.Create("steam.exe", SiteAction.Direct),
                ProgramRoutingPlan.Create(@"C:\Games\Dota\dota2.exe", SiteAction.Direct, allInFolder: true),
                ProgramRoutingPlan.Create("telegram.exe", SiteAction.Tunnel) with { Domain = "t.me" }];
            var (back, apps) = RulesFile.Import(RulesFile.Export(sites, programs));
            Assert(back.SequenceEqual(sites) && apps.Select(a => a.Key).SequenceEqual(programs.Select(p => p.Key)) && apps.Select(a => a.Action).SequenceEqual(programs.Select(p => p.Action)));
            foreach (var bad in new[] { "[]", "{\"oblachka-rules\":2}", "{\"oblachka-rules\":1,\"sites\":[{\"target\":\"0.0.0.0/0\",\"action\":\"direct\"}]}",
                "{\"oblachka-rules\":1,\"sites\":[{\"target\":\"example.com\",\"action\":\"steal\"}]}", "{\"oblachka-rules\":1,\"programs\":[{\"executable\":\"..\\x.exe\",\"action\":\"direct\"}]}", "не json" })
                Assert(Throws<UserError>(() => RulesFile.Import(bad)));
        }));

        await check("P2: uTLS default only for third-party links without fp, UDP timeout, direct DNS, HWID off", async () =>
        {
            const string tlsLink = "vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=tls&sni=example.com&type=tcp&encryption=none#t";
            const string fpLink = tlsLink + "&fp=firefox";
            var plain = Profiles.Parse(tlsLink) with { Source = ProfileSource.External };
            try
            {
                Utls.Default = "safari";
                Assert(Utls.For(plain).Outbound["streamSettings"]!["tlsSettings"]!["fingerprint"]!.GetValue<string>() == "safari");
                Assert(plain.Outbound["streamSettings"]!["tlsSettings"]?["fingerprint"] is null);
                Assert(ReferenceEquals(Utls.For(plain with { Source = ProfileSource.Service }).Outbound, plain.Outbound));
                var own = Profiles.Parse("vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=tls&sni=example.com&type=tcp&encryption=none&fp=firefox#f") with { Source = ProfileSource.External };
                Assert(Utls.For(own).Outbound["streamSettings"]!["tlsSettings"]!["fingerprint"]!.GetValue<string>() == "firefox");
                var box = new System.Text.Json.Nodes.JsonObject { ["type"] = "trojan", ["tls"] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = true } };
                Assert(Utls.Apply(box, CoreKind.SingBox, "chrome") && box["tls"]!["utls"]!["fingerprint"]!.GetValue<string>() == "chrome");
            }
            finally { Utls.Default = null; }
            var config = SingBoxTunConfig.Create(Lease(new(UdpTimeout: 15, DirectDns: "77.88.8.8")), ipv6: false);
            Assert((string?)((JsonObject)((JsonArray)config["inbounds"]!)[0]!)["udp_timeout"] == "15m");
            Assert(new TunnelTuning(DirectDns: "77.88.8.8").DirectResolver(() => IPAddress.Parse("10.0.2.3"))!.ToString() == "77.88.8.8");
            Assert(new TunnelTuning().DirectResolver(() => IPAddress.Parse("10.0.2.3"))!.ToString() == "10.0.2.3");
            Assert(AdvancedOptions.CheckDirectDns("127.0.0.1") is not null && AdvancedOptions.CheckDirectDns("dns.yandex") is not null && AdvancedOptions.CheckUdpTimeout(0) is not null);
            Assert(Throws<UserError>(() => NetworkHelperContracts.Validate(Lease(new(DirectDns: "224.0.0.1")))));
            var seen = new List<HttpRequestMessage>();
            using var client = new HttpClient(new RecordingHandler(seen, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tlsLink) }));
            try
            {
                Subscriptions.ExternalHwid = false;
                await Subscriptions.FetchWithClientAsync(client, "https://sub.other-service.invalid/sub/abcdefghij", CancellationToken.None);
                Assert(!seen[^1].Headers.Contains("x-hwid"));
                await Subscriptions.FetchWithClientAsync(client, "https://" + ServiceBrand.SubscriptionHosts[0] + ServiceBrand.SubscriptionPath + "abcdefghij", CancellationToken.None);
                Assert(seen[^1].Headers.Contains("x-hwid"));
            }
            finally { Subscriptions.ExternalHwid = true; }
        });

        await check("Bypass subnets are opened to programs by protection, and the service rejects bad tuning", () => Sync(() =>
        {
            var backend = new WfpProtectionBackend(() => new FakeWfp(), @"C:\app\tools");
            var change = backend.Prepare(NetworkHelperContracts.Validate(Lease(new(Bypass: ["10.20.1.0/24"]))))[0];
            Assert(change.After.Contains(" bypass=10.20.1.0/24 "));
            var engine = new FakeWfp();
            new WfpProtectionBackend(() => engine, @"C:\app\tools").Apply(Guid.NewGuid(), change);
            var bypass = engine.Filters.Values.Where(f => f.Name.Contains("подсети мимо туннеля")).ToArray();
            Assert(bypass.Length == 1 && bypass[0].Permit && !bypass[0].V6 && bypass[0].Conditions.Single() is { Kind: WfpConditionKind.RemoteSubnet, Number: 24 });
            // Подмена строки журнала на «весь интернет» не проходит.
            var forged = change with { After = change.After.Replace("10.20.1.0/24", "0.0.0.0/0") };
            Assert(Throws<UserError>(() => backend.Apply(Guid.NewGuid(), forged)));
            // До ядра настройки доходят: служба пересобирает запрос по аренде, бэкенд переносит аренду
            // в ядро — оба места раньше теряли новые поля.
            var tuned = NetworkHelperContracts.Validate(Lease(new(TunnelStack.Mixed, 1400, "https://9.9.9.9/dns-query", ["10.20.0.0/16"])));
            Assert(SingBoxTunnelProcess.RequestFor(tuned).Tuning is { Mtu: 1400, Stack: TunnelStack.Mixed });
            ValidatedNetworkLease? started = null; var up = false;
            using (var tunnel = new WindowsTunnelBackend((l, _) => { started = l; up = true; return new FakeTunnelProcess(); }, _ => up, delay: static (_, _) => Task.CompletedTask))
                tunnel.Apply(tuned.LeaseId, tunnel.Prepare(tuned).Single());
            Assert(started?.Tuning is { Dns: "https://9.9.9.9/dns-query" } carried && carried.Subnets.Single() == "10.20.0.0/16");
            foreach (var bad in new TunnelTuning[] { new(Mtu: 100), new(Dns: "http://dns.example/"), new(Dns: "tls://9.9.9.9"), new(Bypass: ["0.0.0.0/1"]), new((TunnelStack)9) })
                Assert(Throws<UserError>(() => NetworkHelperContracts.Validate(Lease(bad))));
            Assert(Throws<UserError>(() => NetworkHelperContracts.Validate(new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, [], Tuning: new(Mtu: 1500)))));
        }));

        await check("LAN proxy: a password-only inbound on all addresses, the computer itself unreachable, service re-checks", () => Sync(() =>
        {
            var options = AdvancedOptions.Default with { LanProxy = true, LanPassword = AdvancedOptions.NewLanPassword() };
            Assert(options.Problem() is null && !options.IsDefault && AdvancedOptions.CheckLanPassword(options.LanPassword) is null);
            var tuning = TunnelTuning.From(options)!;
            Assert(tuning.LanPort == AdvancedOptions.DefaultLanPort && tuning.LanPassword == options.LanPassword);
            // Выключенный прокси пароль хранит, но в туннель не уходит.
            Assert(TunnelTuning.From(options with { LanProxy = false }) is null);
            var config = SingBoxTunConfig.Create(SingBoxTunnelProcess.RequestFor(NetworkHelperContracts.Validate(Lease(tuning))), ipv6: false);
            var lan = ((JsonArray)config["inbounds"]!).OfType<JsonObject>().Single(i => (string?)i["tag"] == TunnelTuning.LanInbound);
            Assert((string?)lan["listen"] == "0.0.0.0" && (int?)lan["listen_port"] == AdvancedOptions.DefaultLanPort && (string?)lan["users"]![0]!["password"] == options.LanPassword);
            var rules = ((JsonArray)config["route"]!["rules"]!).OfType<JsonObject>().ToArray();
            Assert((string?)rules[0]["action"] == "resolve" && (string?)rules[1]["action"] == "reject" && rules[1]["ip_cidr"]!.ToJsonString().Contains("127.0.0.0/8"));
            Assert(rules.Take(2).All(r => r["inbound"]!.ToJsonString() == "[\"lan\"]"));
            Assert(TunnelTuning.From(AdvancedOptions.Default) is null && SingBoxTunConfig.Create(Lease(null), ipv6: false)["inbounds"]!.AsArray().Count == 1);
            foreach (var bad in new TunnelTuning[] { new(LanPort: 10810), new(LanPort: 10810, LanPassword: "short"), new(LanPort: 80, LanPassword: "abcdefghijkl12"), new(LanPassword: "abcdefghijkl12"), new(LanPort: 10808, LanPassword: "abcdefghijkl12") })
                Assert(Throws<UserError>(() => NetworkHelperContracts.Validate(Lease(bad))));
            Assert((options with { ProxyPort = AdvancedOptions.DefaultLanPort }).Problem() is not null && (options with { LanPassword = null }).Problem() is not null);
        }));

        await check("Own geosite and geoip lists: addresses and geoip go to IP lists, junk is refused", () => Sync(() =>
        {
            var custom = RoutingProfile.Custom(["geoip:RU", "geosite:category-ru", "10.1.0.0/16"], ["geosite:youtube", "domain:Example.com"], ["geosite:category-ads-all@cn"])!;
            Assert(custom.DirectSites.SequenceEqual(["geosite:category-ru"]) && custom.DirectIp.SequenceEqual(["geoip:ru", "10.1.0.0/16"]));
            Assert(custom.ProxySites.SequenceEqual(["geosite:youtube", "domain:example.com"]) && custom.BlockSites.SequenceEqual(["geosite:category-ads-all"]));
            Assert(custom.GeositeTags.Count() == 3 && custom.GeoipTags.Single() == "ru" && RoutingProfile.Custom([], [], []) is null);
            Assert(AdvancedOptions.CheckGeo(["geosite:netflix", "geoip:private", "full:a.example", "1.2.3.4"]) is null);
            Assert(AdvancedOptions.CheckGeo(["geosite:"]) is not null && AdvancedOptions.CheckGeo(["ftp:x"]) is not null && AdvancedOptions.CheckGeo(Enumerable.Repeat("geoip:ru", 101).ToArray()) is not null);
            var options = AdvancedOptions.Default with { GeoBlock = ["geosite:category-ads-all"] };
            Assert(!options.IsDefault && options.Problem() is null && options.Changed().Contains("GeoLists:1") && (options with { GeoProxy = ["bad:entry"] }).Problem() is not null);
        }));

        await check("Editing an own server goes through the import checks and keeps its name and group", () => Sync(() =>
        {
            var own = Profiles.Parse("vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=tls&sni=example.com&type=tcp&encryption=none#Дом") with { Group = ProfileGroup.ManualId };
            var text = own.Outbound.ToJsonString();
            var edited = MainWindow.ParseEdited(text.Replace("example.com", "edited.example").Replace("\"tcp\"", "\"ws\""), own);
            Assert(edited is { Name: "Дом", Group: ProfileGroup.ManualId, Transport: "WS / TLS" } && edited.Outbound.ToJsonString().Contains("edited.example"));
            // Отключённая проверка сертификата, повтор ключа, лишняя привязка, другой формат и не-JSON — отказ.
            string With(Action<JsonObject> change) { var copy = (JsonObject)own.Outbound.DeepClone(); change(copy); return copy.ToJsonString(); }
            var insecure = With(o => o["streamSettings"]!["tlsSettings"]!["allowInsecure"] = true);
            var bound = With(o => o["sendThrough"] = "10.0.0.1");
            foreach (var bad in new[] { insecure, bound, "{\"tag\":1,\"tag\":2}", "{\"type\":\"vless\",\"server\":\"a.example\",\"server_port\":443}", "[1]", "{" })
                Assert(Throws<UserError>(() => MainWindow.ParseEdited(bad, own)));
        }));
    }
}

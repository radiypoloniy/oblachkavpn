using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private static Profile Node(string host, string network = "tcp", string sni = "mask.example.org", string name = "N") =>
        Profiles.Parse($"vless://{Uuid}@{host}:443?security=reality&sni={sni}&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&type={network}#{name}");

    private static async Task ResilienceChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Resilience: diagnosis tells a blocked address, a cut handshake, a broken tunnel and UDP apart", async () =>
        {
            var node = Node("203.0.113.40");
            ConnectionDiagnosis.TcpCheck down = (_, _, _) => Task.FromResult<long?>(null), up = (_, _, _) => Task.FromResult<long?>(40);
            ConnectionDiagnosis.TlsCheck cut = (_, _, _, _) => Task.FromResult(false), passes = (_, _, _, _) => Task.FromResult(true);
            Assert((await ConnectionDiagnosis.RunAsync(node, CancellationToken.None, down, passes)).Kind == Blockage.Address);
            Assert((await ConnectionDiagnosis.RunAsync(node, CancellationToken.None, up, cut)).Kind == Blockage.Handshake);
            Assert((await ConnectionDiagnosis.RunAsync(node, CancellationToken.None, up, passes)).Kind == Blockage.Tunnel);
            Assert((await ConnectionDiagnosis.RunAsync(node, CancellationToken.None, (_, _, _) => Task.FromResult<long?>(ServerPing.Blocked), passes)).Kind == Blockage.Unknown);
            Assert((await ConnectionDiagnosis.RunAsync(Profiles.Parse(hy2Link), CancellationToken.None, down, cut)).Kind == Blockage.Udp);
            // Без TLS рукопожатие не проверяется: TCP есть — значит, дело в туннеле.
            string? seen = null;
            Assert((await ConnectionDiagnosis.RunAsync(Profiles.Parse(SsLink), CancellationToken.None, up, (_, _, n, _) => { seen = n; return Task.FromResult(false); })).Kind == Blockage.Tunnel && seen is null);
            Assert(ConnectionDiagnosis.ServerName(node) == "mask.example.org");
        });
        await check("Resilience: the ladder orders attempts by the diagnosis and cuts the handshake where it helps", () => Sync(() =>
        {
            // 0 — упавший RAW, 1 — XHTTP на том же адресе, 2 — RAW на другом адресе с другим SNI, 3 — Hysteria2 на том же адресе.
            var all = new[] { Node("203.0.113.40"), Node("203.0.113.40", "xhttp", name: "X"), Node("203.0.113.41", sni: "other.example.org", name: "B"),
                Profiles.Parse(hy2Link.Replace("example.com:443", "203.0.113.40:443")) };
            var order = new[] { 0, 1, 2, 3 };
            string Plan(Blockage kind, FragmentMode mode = FragmentMode.Auto) =>
                string.Join(",", RouteLadder.Plan(all, order, all[0], kind, mode).Select(s => s.Index + (s.Fragment ? "f" : "")));
            void Expect(string actual, string expected) { if (actual != expected) throw new InvalidOperationException("ladder " + actual + " != " + expected); }
            Expect(Plan(Blockage.Address), "2");
            // Внутри шага сначала та же машина (ServerMachines): Hysteria2 на том же адресе раньше другого сервера.
            Expect(Plan(Blockage.Handshake), "0f,3,2,1f,2f,0");
            Expect(Plan(Blockage.Tunnel), "1,3,2,0,0f");
            Expect(Plan(Blockage.Unknown), "1,3,2,0,0f");
            Expect(Plan(Blockage.Handshake, FragmentMode.Off), "3,2,0");
            Expect(Plan(Blockage.Tunnel, FragmentMode.Always), "1f,3,2f,0f");
            Assert(RouteLadder.Family(all[0]) != RouteLadder.Family(all[1]));
        }));
        await check("Resilience: handshake cutting is added to Xray and sing-box configs, skipped for UDP and plain protocols, and accepted by both cores", async () =>
        {
            var tools = Path.Combine(AppContext.BaseDirectory, "tools");
            var xray = new CoreAdapter(CoreKind.Xray, Path.Combine(tools, "xray.exe"));
            var reality = xray.RuntimeConfig(Node("203.0.113.40").Outbound, 34600);
            Assert(TlsFragment.Apply(reality, CoreKind.Xray));
            Assert(reality["outbounds"]![0]!["streamSettings"]!["sockopt"]!["dialerProxy"]!.GetValue<string>() == TlsFragment.XrayTag &&
                   reality["outbounds"]!.AsArray().Any(o => o!["tag"]!.GetValue<string>() == TlsFragment.XrayTag));
            await CoreConfigurationCheck.ValidateAsync(xray, reality, CancellationToken.None);
            var singBox = new CoreAdapter(CoreKind.SingBox, Path.Combine(tools, "sing-box.exe"));
            var anytls = singBox.RuntimeConfig(Profiles.Parse(AnyTlsLink).Outbound, 34601);
            Assert(TlsFragment.Apply(anytls, CoreKind.SingBox) && anytls["outbounds"]![0]!["tls"]!["fragment"]!.GetValue<bool>());
            await CoreConfigurationCheck.ValidateAsync(singBox, anytls, CancellationToken.None);
            foreach (var plain in new[] { hy2Link, TuicLink, SsLink, WgLink })
            {
                var p = Profiles.Parse(plain);
                Assert(!TlsFragment.Applies(p) && !TlsFragment.Apply(singBox.RuntimeConfig(p.Outbound, 34602), CoreKind.SingBox));
            }
            // WireGuard в endpoints: правила сайтов находят туннель по метке, а не первым выходом.
            var wg = RoutingPlan.Compile(singBox, Profiles.Parse(WgLink), 34603, new[] { RoutingPlan.Create("example.org", SiteAction.Direct) });
            await CoreConfigurationCheck.ValidateAsync(singBox, wg, CancellationToken.None);
        });
        await check("Physical binding: Xray binds the socket that reaches the server (fragment chain included), sing-box detects itself, both cores accept it", async () =>
        {
            var tools = Path.Combine(AppContext.BaseDirectory, "tools");
            var xray = new CoreAdapter(CoreKind.Xray, Path.Combine(tools, "xray.exe"));
            JsonNode? Sockopt(JsonObject config, string tag) => config["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == tag)!["streamSettings"]?["sockopt"];
            var plain = xray.RuntimeConfig(Node("203.0.113.41").Outbound, 34610);
            Assert(PhysicalBinding.Apply(plain, CoreKind.Xray, () => "Ethernet") == "Ethernet");
            Assert(Sockopt(plain, "tunnel")!["interface"]!.GetValue<string>() == "Ethernet");
            await CoreConfigurationCheck.ValidateAsync(xray, plain, CancellationToken.None);
            // «Обход DPI»: сокет открывает freedom из dialerProxy — привязан он, у выхода туннеля только цепочка.
            var cut = xray.RuntimeConfig(Node("203.0.113.42").Outbound, 34611);
            Assert(TlsFragment.Apply(cut, CoreKind.Xray) && PhysicalBinding.Apply(cut, CoreKind.Xray, () => "Wi-Fi") == "Wi-Fi");
            Assert(Sockopt(cut, TlsFragment.XrayTag)!["interface"]!.GetValue<string>() == "Wi-Fi" && Sockopt(cut, "tunnel")!["interface"] is null);
            await CoreConfigurationCheck.ValidateAsync(xray, cut, CancellationToken.None);
            // Без сети привязать не к чему: запуск отказывает, а не гоняет трафик ядра через туннель.
            ExpectUserError(() => PhysicalBinding.Apply(xray.RuntimeConfig(Node("203.0.113.43").Outbound, 34612), CoreKind.Xray, () => null));
            var singBox = new CoreAdapter(CoreKind.SingBox, Path.Combine(tools, "sing-box.exe"));
            var hy2 = RoutingPlan.Compile(singBox, Profiles.Parse(hy2Link), 34613, new[] { RoutingPlan.Create("example.org", SiteAction.Direct) },
                directEntry: CoreDirectEntry.Create(34614));
            Assert(PhysicalBinding.Apply(hy2, CoreKind.SingBox, () => throw new InvalidOperationException("sing-box picks the interface itself")) is null);
            Assert(hy2["route"]!["auto_detect_interface"]!.GetValue<bool>());
            await CoreConfigurationCheck.ValidateAsync(singBox, hy2, CancellationToken.None);
        });
        await check("Resilience: route search follows ladder steps, switches handshake cutting per step and reports which one worked", async () =>
        {
            var started = new List<string>();
            var cut = false;
            var search = new RouteSearch((p, _) => { started.Add(p.Name + (cut ? "+f" : "")); return Task.CompletedTask; },
                (_, _) => Task.FromResult(cut && started[^1] == "N+f" ? new ProbeResult(true, 90, "ok") : new ProbeResult(false, 0, "no", ProbeFailure.Reachability)),
                () => { }, value => cut = value);
            var profiles = new[] { Node("203.0.113.40"), Node("203.0.113.41", name: "B") };
            var result = await search.FindAsync(profiles, new[] { new LadderStep(1, false), new LadderStep(0, true) }, TunnelHealth.Targets[0], CancellationToken.None);
            Assert(string.Join(",", started) == "B,N+f" && result.ProfileIndex == 0 && result.Fragment && result.Attempts[^1].Fragment);
        });
    }
}

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientBlockChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Server measurements own a separate core and cancel without a second start", async () =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls); var samples = new List<ServerMeasurement>();
            using var cancel = new CancellationTokenSource();
            try
            {
                await ServerMeasurements.MeasureAsync([Profiles.Parse(link), Profiles.Parse(link.Replace("example.com", "example.net"))],
                    () => core, sample => { samples.Add(sample); cancel.Cancel(); }, cancel.Token);
                throw new InvalidOperationException("Expected cancellation");
            }
            catch (OperationCanceledException) { Assert(samples.Count == 1 && samples[0].Success && core.ProcessId is null && calls.Count(c => c == "core:start") == 1); }
        });
        await check("Program routing rejects paths and duplicate names; pinned sing-box validates TUN draft", async () =>
        {
            ExpectUserError(() => ProgramRoutingPlan.Create("C:\\private\\steam.exe", SiteAction.Direct));
            ExpectUserError(() => ProgramRoutingPlan.Create("steam.exe;whoami", SiteAction.Direct));
            ExpectUserError(() => ProgramRoutingPlan.Validate([new("Steam.exe", SiteAction.Direct), new("steam.exe", SiteAction.Block)]));
            var config = ProgramRoutingPlan.Compile(NetworkAcquire(), [new("steam.exe", SiteAction.Direct), new("browser.exe", SiteAction.Block)]);
            // Перехват DNS остаётся первым правилом, пользовательские идут следом.
            // Прежде между ними стоял запрет UDP; теперь UDP проходит в туннель.
            Assert(config["route"]!["rules"]![0]!["action"]!.GetValue<string>() == "hijack-dns");
            Assert(config["route"]!["rules"]![1]!["outbound"]!.GetValue<string>() == "user-direct");
            Assert(config["route"]!["rules"]![2]!["action"]!.GetValue<string>() == "reject");
            await CoreConfigurationCheck.ValidateAsync(new(CoreKind.SingBox, Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe")), config, CancellationToken.None);
        });
        await check("Routing drafts reject URLs and duplicates, prioritize subdomains, validate both cores", async () =>
        {
            ExpectUserError(() => RoutingPlan.Create("https://example.com/path", SiteAction.Direct));
            // Одиночный адрес — правило подсети /32 (SiteTarget), а не отказ.
            Assert(RoutingPlan.Create("192.0.2.1", SiteAction.Block).Domain == "192.0.2.1/32");
            var rule = RoutingPlan.Create("EXAMPLE.COM.", SiteAction.Block); Assert(rule.Domain == "example.com");
            ExpectUserError(() => RoutingPlan.Validate([rule, rule]));
            SiteRule[] rules = [rule, RoutingPlan.Create("sub.example.com", SiteAction.Tunnel), RoutingPlan.Create("direct.example.net", SiteAction.Direct),
                RoutingPlan.Create("10.20.0.0/16", SiteAction.Direct), RoutingPlan.Create("port:27015-27030", SiteAction.Direct), RoutingPlan.Create("port:25", SiteAction.Block)];
            foreach (var core in new[] { CoreKind.Xray, CoreKind.SingBox })
            {
                var profile = Profiles.Parse(core == CoreKind.Xray ? link : link.Replace("type=xhttp&path=%2Fexample&mode=stream-up&", "type=tcp&"));
                if (core == CoreKind.SingBox) profile = SingBoxProfiles.Parse(new System.Text.Json.Nodes.JsonObject { ["type"] = "vless", ["server"] = "example.com", ["server_port"] = 443, ["uuid"] = "11111111-1111-4111-8111-111111111111", ["tls"] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = true, ["server_name"] = "example.com" } }, "fixture");
                var adapter = new CoreAdapter(core, Path.Combine(AppContext.BaseDirectory, "tools", core == CoreKind.Xray ? "xray.exe" : "sing-box.exe"));
                var config = RoutingPlan.Compile(adapter, profile, 10808, rules);
                if (core == CoreKind.Xray) Assert(config["routing"]!["rules"]![0]!["domain"]![0]!.GetValue<string>() == "domain:sub.example.com");
                await CoreConfigurationCheck.ValidateAsync(adapter, config, CancellationToken.None);
            }
        });
        await check("Diagnostic report excludes arbitrary outcome strings and contains only event codes", () => Sync(() =>
        {
            var report = DiagnosticReport.Create(ConnectionState.Running, 2, 1, [new(DateTimeOffset.UtcNow, ClientEventCode.Verified)], [new(1, "SECRET-https://server.invalid/token", 5)]);
            Assert(!report.Contains("SECRET") && !report.Contains("server.invalid") && report.Contains("Unknown"));
            using var systemReport = System.Text.Json.JsonDocument.Parse(DiagnosticReport.Create(ConnectionState.Running, 2, 1, [], [], DiagnosticMode.SystemTunnel, true));
            Assert(systemReport.RootElement.GetProperty("Mode").GetString() == "SystemTunnel" &&
                systemReport.RootElement.GetProperty("SystemNetworkModified").GetBoolean() && systemReport.RootElement.GetProperty("BlocksBypass").GetBoolean());
        }));
        await check("Server selection ignores stale failed and future measurements", () => Sync(() =>
        {
            var profiles = new[] { Profiles.Parse(link), Profiles.Parse(link.Replace("example.com", "example.net")) };
            var now = DateTimeOffset.UtcNow; var first = Profiles.Fingerprint(profiles[0]); var second = Profiles.Fingerprint(profiles[1]);
            var cache = new Dictionary<string, ServerMeasurement> { [first] = new(first, now.AddMinutes(-21), true, 1), [second] = new(second, now, true, 10) };
            Assert(ServerMeasurements.Best(profiles, cache, now) == 1);
            cache[second] = cache[second] with { Time = now.AddMinutes(1) }; Assert(ServerMeasurements.Best(profiles, cache, now) is null);
        }));
        await check("Encrypted state preserves subscription settings metadata rules and measurements", () => Sync(() =>
        {
            var profile = Profiles.Parse(link); var key = Profiles.Fingerprint(profile); var store = new ClientStateStore(Path.Combine(root, "blocks-state.bin"));
            store.Save(new(1, [new(profile.Name, profile.Outbound)], key, ["https://fixture.invalid/SECRET-token"], 0, false, true)
                { Favorites = [key], AutoRefresh = false, Metadata = new(1, 2, 100, null), RouteRules = [new("secret-domain.invalid", SiteAction.Direct)], ProgramRules = [new("steam.exe", SiteAction.Direct)], Measurements = [new(key, DateTimeOffset.UtcNow, true, 12)] });
            var loaded = store.Load()!; Assert(!loaded.AutoRefresh && loaded.Metadata?.Total == 100 && loaded.RouteRules.Length == 1 && loaded.ProgramRules.Length == 1 && loaded.Measurements[0].Milliseconds == 12);
            Assert(loaded.Favorites.SequenceEqual(new[] { key }));
            var bytes = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "blocks-state.bin"))); Assert(!bytes.Contains("SECRET-token") && !bytes.Contains("secret-domain"));
        }));
    }
}

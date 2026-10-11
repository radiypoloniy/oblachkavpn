namespace RoutePilot;

public static partial class SelfTests
{
    private static async Task ClientBlockChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Routing drafts reject URLs and duplicates, prioritize subdomains, validate both cores", async () =>
        {
            ExpectUserError(() => RoutingPlan.Create("https://example.com/path", SiteAction.Direct));
            ExpectUserError(() => RoutingPlan.Create("192.0.2.1", SiteAction.Block));
            var rule = RoutingPlan.Create("EXAMPLE.COM.", SiteAction.Block); Assert(rule.Domain == "example.com");
            ExpectUserError(() => RoutingPlan.Validate([rule, rule]));
            SiteRule[] rules = [rule, RoutingPlan.Create("sub.example.com", SiteAction.Tunnel), RoutingPlan.Create("direct.example.net", SiteAction.Direct)];
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
                { AutoRefresh = false, Metadata = new(1, 2, 100, null), RouteRules = [new("secret-domain.invalid", SiteAction.Direct)], Measurements = [new(key, DateTimeOffset.UtcNow, true, 12)] });
            var loaded = store.Load()!; Assert(!loaded.AutoRefresh && loaded.Metadata?.Total == 100 && loaded.RouteRules.Length == 1 && loaded.Measurements[0].Milliseconds == 12);
            var bytes = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "blocks-state.bin"))); Assert(!bytes.Contains("SECRET-token") && !bytes.Contains("secret-domain"));
        }));
    }
}

using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // Бесплатные серверы «на свой риск» (FreeServers, ForeignGuard): отбор, исключение из автовыбора
    // и запасных, заслоны в обоих режимах.
    private static async Task FreeServerChecksAsync(Func<string, Func<Task>, Task> check)
    {
        const string Vless = "vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=reality&sni=www.example.com&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&fp=chrome&type=tcp&encryption=none";
        await check("Free lists: own names, no repeats by address, jsDelivr fallback, long lists trimmed", () => Sync(() =>
        {
            var parsed = Subscriptions.Parse(Vless + "#%F0%9F%87%AB%F0%9F%87%AE%20ad%20t.me%2Fspam\n" + Vless + "#copy\n" + Vless.Replace("203.0.113.7", "203.0.113.8") + "#x").Profiles;
            var free = FreeServers.Prepare(parsed);
            Assert(free.Count == 2 && free.All(p => p.IsFree && p.Group == FreeServers.GroupId && p.Name.StartsWith("Бесплатный · ")));
            Assert(!free.Any(p => p.Name.Contains("t.me") || p.Name.Contains("ad")));
            Assert(free[0].Name == "Бесплатный · Финляндия · 1" && free[0].Country.Code == "FI");
            Assert(FreeServers.Urls("igareck/vpn-configs-for-russia/main/BLACK_VLESS_RUS.txt")[1] == "https://cdn.jsdelivr.net/gh/igareck/vpn-configs-for-russia@main/BLACK_VLESS_RUS.txt");
            var long_ = string.Join('\n', Enumerable.Range(0, 260).Select(i => Vless + "#n" + i));
            Assert(Subscriptions.Parse(FreeServers.Trim(long_)).Profiles.Count == Subscriptions.MaxProfiles);
            // Настоящей проверкой через сервер проверяется больше, чем показывается: отбор — из работающих.
            Assert(FreeServers.Checked > FreeServers.Shown);
            // extra=null у XHTTP (так их выкладывает zieng2) — как без extra; объект по-прежнему читается.
            const string Xhttp = "vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=reality&sni=www.example.com&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&type=xhttp&path=%2Fx&encryption=none";
            Assert(Profiles.Parse(Xhttp + "&extra=null#N").Outbound.ToJsonString().Contains("xhttpSettings") && !Profiles.Parse(Xhttp + "&extra=null#N").Outbound.ToJsonString().Contains("\"extra\""));
            Assert(Profiles.Parse(Xhttp + "&extra=%7B%22xPaddingBytes%22%3A%22100-1000%22%7D#E").Outbound.ToJsonString().Contains("xPaddingBytes"));
            ExpectUserError(() => Profiles.Parse(Xhttp + "&extra=%5B1%5D#A"));
        }));
        await check("Free nodes never join auto-select, spares or recovery unless chosen by hand", () => Sync(() =>
        {
            var service = Profiles.Parse(Vless + "#S") with { Source = ProfileSource.Service };
            var free = FreeServers.Prepare([Profiles.Parse(Vless.Replace("203.0.113.7", "203.0.113.9") + "#F")])[0];
            var pool = new AutoPool();
            pool.Set(free, true);
            Assert(!pool.Contains(free) && pool.Snapshot([free]).Count == 0);
            pool.Set(service, false);
            Assert(pool.Members([service, free]).SequenceEqual([service]));
            var all = new[] { service, free };
            Assert(RouteCandidates.For(all, service, anyNode: true).SequenceEqual([0]));
            Assert(RouteCandidates.For(all, free, anyNode: true).SequenceEqual([0, 1]) && RouteCandidates.For(all, free).SequenceEqual([1]));
        }));
        await check("Free node guard: HTTP and service domains blocked in both cores; cores accept it", async () =>
        {
            var tools = Path.Combine(AppContext.BaseDirectory, "tools");
            var xrayProfile = Profiles.Parse(Vless + "#F") with { Source = ProfileSource.Free };
            var xray = new CoreAdapter(CoreKind.Xray, Path.Combine(tools, "xray.exe"));
            var config = RoutingPlan.Compile(xray, xrayProfile, 10808, [RoutingPlan.Create("oblaconnection.com", SiteAction.Tunnel)]);
            var rules = config["routing"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
            Assert((string?)rules[0]["port"] == "80" && (string?)rules[0]["outboundTag"] == "user-block");
            Assert(rules.Any(r => r["domain"]?.ToJsonString().Contains("oblaconnection.com") == true && (string?)r["outboundTag"] == "user-block"));
            Assert(!rules.Any(r => r["domain"]?.ToJsonString().Contains("oblaconnection.com") == true && (string?)r["outboundTag"] == "tunnel"));
            await CoreConfigurationCheck.ValidateAsync(xray, config, CancellationToken.None);
            Assert(!RoutingPlan.Compile(xray, xrayProfile with { Source = ProfileSource.External }, 10808, []).ToJsonString().Contains("\"80\""));

            var singBox = new CoreAdapter(CoreKind.SingBox, Path.Combine(tools, "sing-box.exe"));
            var hy2 = Profiles.Parse("hysteria2://secret@203.0.113.7:443?sni=example.com#H") with { Source = ProfileSource.Free };
            var box = RoutingPlan.Compile(singBox, hy2, 10808, []);
            var boxRules = box["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
            Assert(boxRules.Any(r => (int?)r["port"] == 80 && (string?)r["action"] == "reject"));
            Assert(boxRules.Any(r => r["domain_suffix"]?.ToJsonString().Contains("oblaconnection.com") == true && (string?)r["action"] == "reject"));
            await CoreConfigurationCheck.ValidateAsync(singBox, box, CancellationToken.None);

            var request = NetworkAcquire() with { ForeignNode = true, Sites = ForeignGuard.With([], 100) };
            await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None);
            var tun = SingBoxTunConfig.Create(request)["route"]!["rules"]!.ToJsonString();
            Assert(tun.Contains("\"port\":80") && !SingBoxTunConfig.Create(NetworkAcquire()).ToJsonString().Contains("\"port\":80"));
            var lease = NetworkHelperContracts.Validate(request);
            Assert(lease.ForeignNode && SingBoxTunnelProcess.RequestFor(lease).ForeignNode && lease.SiteRules.All(r => r.Action == SiteAction.Block));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, Array.Empty<string>(), ForeignNode: true)));
        });
    }
}

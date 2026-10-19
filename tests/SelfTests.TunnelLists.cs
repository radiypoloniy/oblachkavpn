using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private static CompiledRouting ListsFixture(bool globalProxy = true)
    {
        var direct = new RouteMatchers(); direct.Suffix.UnionWith(["ru", "xn--p1ai", "yandex.net"]); direct.Full.Add("gosuslugi.ru"); direct.Keyword.Add("sber"); direct.Regex.Add(@"^vk\.(com|ru)$"); direct.Cidr.UnionWith(["5.255.255.0/24", "2a02:6b8::/32"]);
        var proxy = new RouteMatchers(); proxy.Suffix.Add("youtube.com");
        var block = new RouteMatchers(); block.Suffix.Add("ads.example");
        return new(direct, proxy, block, globalProxy, ["block", "direct", "proxy"], []);
    }

    private static async Task TunnelListsChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Routing lists split into service-sized parts; the service rejects unsafe entries", () => Sync(() =>
        {
            var big = ListsFixture(); big.Direct.Cidr.UnionWith(Enumerable.Range(0, 20000).Select(i => "10." + (i >> 8 & 255) + "." + (i & 255) + ".0/24"));
            var parts = RoutingListsRequest.Split(big).ToArray();
            Assert(parts.Length > 1 && parts.Count(p => p.Last) == 1 && parts[^1].Last && parts.Select(p => p.Part).SequenceEqual(Enumerable.Range(0, parts.Length)));
            Assert(parts.Where(p => p.Set == "direct" && p.Kind == "cidr").Sum(p => p.Items.Length) == 20002);
            foreach (var part in parts)
            {
                part.Validate();
                var request = new HelperRequest(1, Guid.NewGuid(), HelperOperation.RoutingLists, null, Lists: part);
                HelperProtocol.Validate(request);
                Assert(System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(request)) < HelperProtocol.MaxBytes);
            }
            foreach (var (kind, bad) in new[] { ("regex", "(?=x)a"), ("regex", @"(a)\1"), ("cidr", "10.0.0.0/99"), ("keyword", "a b"), ("suffix", "../x"), ("full", "\"x") })
                ExpectUserError(() => new RoutingListsRequest(0, true, "direct", kind, [bad]).Validate());
            ExpectUserError(() => new RoutingListsRequest(0, true, "elsewhere", "suffix", ["ru"]).Validate());
            ExpectUserError(() => HelperProtocol.Validate(new(1, Guid.NewGuid(), HelperOperation.DirectDns, null, DirectDns: new(0, true, []), Lists: parts[0])));
            ExpectUserError(() => HelperProtocol.Validate(new(1, Guid.NewGuid(), HelperOperation.RoutingLists, null)));
        }));
        await check("Tunnel core runs routing lists from service files after user rules; sing-box accepts them", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "oblachka-lists-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var set in RoutingListsRequest.Sets)
                {
                    var matchers = set switch { "direct" => ListsFixture().Direct, "proxy" => ListsFixture().Proxy, _ => ListsFixture().Block };
                    RoutingLists.Write(folder, set, kind => (kind switch { "suffix" => matchers.Suffix, "full" => matchers.Full, "keyword" => matchers.Keyword, "regex" => matchers.Regex, _ => matchers.Cidr }).ToArray());
                }
                var direct = JsonNode.Parse(File.ReadAllText(RoutingLists.PathOf(folder, "direct")))!;
                Assert(direct["rules"]!.AsArray().Count == 2 && direct["rules"]![1]!["ip_cidr"]!.AsArray().Count == 2 && direct["rules"]![0]!["domain_regex"] is not null);
                var lists = new RoutingListsEntry(["block", "direct", "proxy"], true);
                var request = NetworkAcquire() with { CoreDirect = CoreDirectEntry.Create(23456), Sites = [RoutingPlan.Create("example.org", SiteAction.Tunnel)], Lists = lists };
                await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None, listsFolder: folder);
                var config = SingBoxTunConfig.Create(request, listsFolder: folder);
                var rules = config["route"]!["rules"]!.AsArray().Select(r => r!.AsObject()).ToArray();
                int At(string set) => Array.FindIndex(rules, r => r["rule_set"]?[0]?.GetValue<string>() == RoutingLists.Tag(set));
                var site = Array.FindIndex(rules, r => r["domain_suffix"] is not null);
                Assert(rules.Any(r => r["action"]?.GetValue<string>() == "sniff") && site >= 0 && site < At("block") && At("block") < At("direct") && At("direct") < At("proxy"));
                Assert(rules[At("block")]["action"]!.GetValue<string>() == "reject" && rules[At("direct")]["outbound"]!.GetValue<string>() == SingBoxTunConfig.UserDirect);
                Assert(config["route"]!["final"]!.GetValue<string>() == "local-core");
                // «Остальное напрямую» — итог в прямой выход; без разрешения защиты «напрямую» — блок.
                Assert(SingBoxTunConfig.Create(request with { Lists = lists with { GlobalProxy = false } }, listsFolder: folder)["route"]!["final"]!.GetValue<string>() == SingBoxTunConfig.UserDirect);
                var strict = SingBoxTunConfig.Create(request with { CoreDirect = null, Sites = null }, listsFolder: folder)["route"]!["rules"]!.AsArray();
                Assert(strict.Single(r => r!["rule_set"]?[0]?.GetValue<string>() == RoutingLists.Tag("direct"))!["action"]!.GetValue<string>() == "reject");
                ExpectUserError(() => SingBoxTunConfig.Create(request));
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });
        await check("Lease carries the routing lists order through the service rebuilds", () => Sync(() =>
        {
            var request = NetworkAcquire() with { Lists = new(["direct", "proxy"], true) };
            var lease = NetworkHelperContracts.Validate(request);
            Assert(SingBoxTunnelProcess.RequestFor(lease).Lists is { Order: ["direct", "proxy"] });
            ValidatedNetworkLease? started = null; var up = false;
            using (var tunnel = new WindowsTunnelBackend((l, _) => { started = l; up = true; return new FakeTunnelProcess(); }, _ => up, delay: static (_, _) => Task.CompletedTask))
                tunnel.Apply(lease.LeaseId, tunnel.Prepare(lease).Single());
            Assert(started?.Lists is { GlobalProxy: true });
            ExpectUserError(() => NetworkHelperContracts.Validate(request with { Lists = new(["direct", "direct"], true) }));
            ExpectUserError(() => NetworkHelperContracts.Validate(request with { Lists = new([], true) }));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, [], Lists: new(["direct"], true))));
        }));
    }
}

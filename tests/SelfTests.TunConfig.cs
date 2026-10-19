namespace Oblachka;

public static partial class SelfTests
{
    private static async Task TunConfigurationChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Pinned sing-box accepts TUN bridge configuration without creating interfaces", async () =>
        {
            var request = NetworkAcquire() with { PeerAddresses = new[] { "192.0.2.1", "2001:db8::1" } };
            await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None);
            var config = SingBoxTunConfig.Create(request);
            Assert(config["outbounds"]!.AsArray().Count == 1 && config["outbounds"]![0]!["type"]!.GetValue<string>() == "socks");
            Assert(config["dns"]!["servers"]![0]!["detour"]!.GetValue<string>() == "local-core");
            // Истёкшие адреса — сразу из кэша, обновление в фоне; кэш только в памяти, не на диске.
            Assert(config["dns"]!["optimistic"]!["enabled"]!.GetValue<bool>() && config["dns"]!["cache_capacity"]!.GetValue<int>() >= 4096);
            Assert(config["experimental"]?["cache_file"] is null);
            var exclusions = config["inbounds"]![0]!["route_exclude_address"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Assert(exclusions.SequenceEqual(new[] { "192.0.2.1/32", "2001:db8::1/128" }));
            Assert(!config.ToJsonString().Contains("direct") && !config.ToJsonString().Contains("insecure"));
        });
        await check("TUN builder rejects release, broad exclusions and invalid loopback ports", () => Sync(() =>
        {
            ExpectUserError(() => SingBoxTunConfig.Create(new(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, Array.Empty<string>())));
            ExpectUserError(() => SingBoxTunConfig.Create(NetworkAcquire() with { PeerAddresses = new[] { "0.0.0.0/0" } }));
            ExpectUserError(() => SingBoxTunConfig.Create(NetworkAcquire() with { LoopbackPort = 0 }));
        }));
        await check("Direct sites resolve through the physical network's DNS: rule file, resolver, direct outbound; sing-box accepts it", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "oblachka-dns-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var rules = Path.Combine(folder, "direct-dns.json");
                File.WriteAllText(rules, """{"version":2,"rules":[{"domain_suffix":["ru","youtube.com"]}]}""");
                var request = NetworkAcquire() with { PeerAddresses = new[] { "192.0.2.1" }, CoreDirect = CoreDirectEntry.Create(23456) };
                var dns = System.Net.IPAddress.Parse("192.168.1.1");
                await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None, true, dns, rules);
                var config = SingBoxTunConfig.Create(request, true, dns, rules);
                var local = config["dns"]!["servers"]!.AsArray().Single(s => s!["tag"]!.GetValue<string>() == DirectDns.ServerTag)!;
                Assert(local["server"]!.GetValue<string>() == "192.168.1.1" && local["detour"] is null);
                Assert(config["dns"]!["final"]!.GetValue<string>() == "remote-dns");
                Assert(config["dns"]!["rules"]![0]!["rule_set"]![0]!.GetValue<string>() == DirectDns.RuleSetTag);
                Assert(config["route"]!["rule_set"]![0]!["path"]!.GetValue<string>() == rules);
                var direct = config["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == SingBoxTunConfig.UserDirect)!;
                Assert(direct["domain_resolver"]!.GetValue<string>() == DirectDns.ServerTag);
                // Без прямых правил нечего разрешать мимо туннеля: всё как раньше.
                var plain = SingBoxTunConfig.Create(NetworkAcquire() with { PeerAddresses = new[] { "192.0.2.1" } }, true, dns, rules);
                Assert(!plain.ToJsonString().Contains(DirectDns.ServerTag));
            }
            finally { Directory.Delete(folder, true); }
        });
        await check("Direct site list goes to the service in parts and only as plain domain names", () => Sync(() =>
        {
            Assert(DirectDnsRequest.Valid("ru") && DirectDnsRequest.Valid("xn--p1ai") && DirectDnsRequest.Valid("i.ytimg.com"));
            Assert(!DirectDnsRequest.Valid("*.ru") && !DirectDnsRequest.Valid("a..b") && !DirectDnsRequest.Valid("YouTube.com") && !DirectDnsRequest.Valid("x y"));
            var many = Enumerable.Range(0, 2000).Select(i => "s" + i + ".example.ru").ToArray();
            var parts = DirectDnsRequest.Split(many).ToArray();
            Assert(parts.Length == 3 && parts.Count(p => p.Last) == 1 && parts[^1].Last && parts.Sum(p => p.Domains.Length) == 2000);
            foreach (var part in parts)
            {
                part.Validate();
                var request = new HelperRequest(1, Guid.NewGuid(), HelperOperation.DirectDns, null, DirectDns: part);
                HelperProtocol.Validate(request);
                Assert(System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(request)) < HelperProtocol.MaxBytes);
            }
            Assert(DirectDnsRequest.Split([]).Single() is { Last: true, Domains.Length: 0 });
            ExpectUserError(() => new DirectDnsRequest(0, true, ["evil\"\\path"]).Validate());
            ExpectUserError(() => HelperProtocol.Validate(new(1, Guid.NewGuid(), HelperOperation.Zapret, null, Zapret: new(ZapretAction.Status), DirectDns: parts[0])));
        }));
    }
}

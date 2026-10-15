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
    }
}

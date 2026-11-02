using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task HysteriaDirectChecksAsync(Func<string, Func<Task>, Task> check)
    {
        Profile Profile() => SingBoxProfiles.Parse(new JsonObject
        {
            ["type"] = "hysteria2", ["server"] = "203.0.113.7", ["server_port"] = 443,
            ["password"] = Guid.NewGuid().ToString(),
            ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "example.com", ["alpn"] = new JsonArray("h3") }
        }, "Hysteria fixture");
        var peers = new[] { IPAddress.Parse("203.0.113.7") };
        await check("Native Hysteria2 retains TLS, bandwidth, obfuscation and network restrictions in the protected core", async () =>
        {
            foreach (var decorated in new[] { false, true })
            {
                var p = Profile();
                if (decorated)
                {
                    p.Outbound["up_mbps"] = 50; p.Outbound["down_mbps"] = 200; p.Outbound["network"] = "udp";
                    p.Outbound["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = Guid.NewGuid().ToString() };
                }
                var outbound = DirectOutbound.From(p)!;
                var original = (JsonObject)p.Outbound.DeepClone(); original.Remove("tag");
                Assert(JsonNode.DeepEquals(outbound, original) && JsonNode.DeepEquals(DirectOutboundPolicy.Validate(outbound, peers), original));
                var lease = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["203.0.113.7"],
                    Sites: [new("direct.invalid", SiteAction.Direct)], Stats: CoreStatsEntry.Create(23999), Direct: [outbound]);
                await CoreConfigurationCheck.ValidateTunAsync(lease, CancellationToken.None);
                Assert(NetworkHelperContracts.Validate(lease).HasDirect && SingBoxTunConfig.Create(lease)["route"]!["final"]!.GetValue<string>() == SingBoxTunConfig.ServerSelector);
            }
        });
        await check("Hysteria2 service policy rejects unleased IPs, weak TLS, file references and unknown options", () => Sync(() =>
        {
            var good = DirectOutbound.From(Profile())!;
            void Reject(Action<JsonObject> edit) { var copy = (JsonObject)good.DeepClone(); edit(copy); ExpectUserError(() => DirectOutboundPolicy.Validate(copy, peers)); }
            Reject(o => o["server"] = "198.51.100.9"); Reject(o => o["server"] = "node.invalid");
            Reject(o => o["tls"]!["insecure"] = true); Reject(o => o["tls"]!["enabled"] = false);
            Reject(o => o.Remove("tls")); Reject(o => o["tls"]!["certificate_path"] = "C:\\private.pem");
            Reject(o => o["tls"]!["alpn"] = "h3"); Reject(o => o["detour"] = "direct");
            Reject(o => o["bind_interface"] = "Ethernet"); Reject(o => o["up_mbps"] = "50");
            Reject(o => o["down_mbps"] = -1); Reject(o => o["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = "" });
            Reject(o => o["network"] = "icmp"); Reject(o => o["server_ports"] = new JsonArray("1:65535"));
        }));
        await check("Unsupported Hysteria2 settings keep the separate-core path without deleting TLS pins or port hopping", () => Sync(() =>
        {
            foreach (var decorate in new Action<JsonObject>[]
            {
                o => o["tls"]!["certificate_public_key_sha256"] = new JsonArray(Convert.ToBase64String(new byte[32])),
                o => o["server_ports"] = new JsonArray("443:445"), o => o["hop_interval"] = "30s"
            })
            { var p = Profile(); decorate(p.Outbound); var before = p.Outbound.ToJsonString(); Assert(DirectOutbound.From(p) is null && p.Outbound.ToJsonString() == before); }
        }));
        await check("Hysteria2 session carries the selected server and keeps protection if fallback selection fails", async () =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls); var helper = new SessionHelper(calls);
            await using var session = new SystemTunnelSession(core, helper) { DirectAllowed = () => true };
            var primary = await TunnelEndpoints.PrepareAsync(Profile(), CancellationToken.None);
            var fallback = Profile(); fallback.Outbound["tls"]!["certificate_public_key_sha256"] = new JsonArray(Convert.ToBase64String(new byte[32]));
            var spare = await TunnelEndpoints.PrepareAsync(fallback, CancellationToken.None);
            await session.StartWithSparesAsync([primary, spare], CancellationToken.None);
            Assert(helper.LastRequest!.Direct is [{ }, null] && core.CarriedBy == session.Stats && session.DirectActive);
            await ExpectUserErrorAsync(() => session.SwitchAsync(fallback, CancellationToken.None));
            Assert(core.CarriedBy is null && session.State == SystemTunnelState.Holding && session.BlocksBypass &&
                calls.Count(c => c == "helper:acquire") == 1 && !calls.Contains("helper:release"));
        });
    }
}

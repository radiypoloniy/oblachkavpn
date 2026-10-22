using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // «QUIC через TCP» (0.19): UDP 443 в туннеле отклоняется, локальная сеть остаётся напрямую.
    private static async Task QuicOverTcpChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("QUIC over TCP rejects UDP 443 after DNS and local network; sing-box accepts it", async () =>
        {
            var request = NetworkAcquire() with { QuicOverTcp = true, LocalNetwork = true };
            await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None);
            var rules = SingBoxTunConfig.Create(request)["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
            var reject = rules.Single(r => (string?)r["action"] == "reject");
            Assert((string?)reject["network"] == "udp" && (int?)reject["port"] == 443);
            Assert((string?)rules[0]["action"] == "hijack-dns");
            Assert(rules.IndexOf(reject) > rules.FindIndex(r => r["ip_cidr"] is not null));
            Assert(!SingBoxTunConfig.Create(NetworkAcquire()).ToJsonString().Contains("\"reject\""));
        });
        await check("QUIC over TCP flag travels the lease end to end and is refused on release", () => Sync(() =>
        {
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, Array.Empty<string>(), QuicOverTcp: true)));
            var lease = NetworkHelperContracts.Validate(NetworkAcquire() with { QuicOverTcp = true });
            Assert(lease.QuicOverTcp && SingBoxTunnelProcess.RequestFor(lease).QuicOverTcp);
            // Отклонение не открывает прямой выход: защита та же.
            Assert(!lease.DirectExit);
        }));
    }
}

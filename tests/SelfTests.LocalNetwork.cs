using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // «Локальная сеть напрямую» (0.18): частные подсети идут прямым выходом ядра туннеля.
    private static async Task LocalNetworkChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Local network goes direct from the tunnel core; sing-box accepts it; DNS stays first", async () =>
        {
            var request = NetworkAcquire() with { LocalNetwork = true };
            await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None);
            var config = SingBoxTunConfig.Create(request);
            var rules = config["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToArray();
            Assert((string?)rules[0]["action"] == "hijack-dns");
            var local = rules.Single(r => r["ip_cidr"] is not null);
            Assert((string?)local["outbound"] == SingBoxTunConfig.UserDirect);
            Assert(local["ip_cidr"]!.AsArray().Select(n => (string?)n).SequenceEqual(SingBoxTunConfig.LocalSubnets));
            Assert(config["outbounds"]!.AsArray().Any(o => (string?)o!["tag"] == SingBoxTunConfig.UserDirect));
            // Защита открывает ядру туннеля прямой выход только вместе с флагом.
            Assert(NetworkHelperContracts.Validate(request).DirectExit && !NetworkHelperContracts.Validate(NetworkAcquire()).DirectExit);
            // Смысл правил не меняется: без правил «напрямую» списки и программы прямо не идут.
            Assert(!NetworkHelperContracts.Validate(request).HasDirect);
            // Защита открывает ядру туннеля только частные подсети, а не любой адрес (аудит 19.10, В-1).
            var backend = new WfpProtectionBackend(() => new FakeWfp(), @"C:\app\tools");
            var change = backend.Prepare(NetworkHelperContracts.Validate(request))[0];
            Assert(change.After.Contains(" local=1 ", StringComparison.Ordinal) && !change.After.Contains("direct=1", StringComparison.Ordinal));
            var engine = new FakeWfp();
            new WfpProtectionBackend(() => engine, @"C:\app\tools").Apply(Guid.NewGuid(), change);
            var core = engine.Filters.Values.Where(f => f.Permit && f.Conditions.Any(c => c.Kind == WfpConditionKind.Application && c.Program == backend.TunCoreProgram)).ToArray();
            Assert(core.Length > 0 && core.All(f => f.Conditions.Any(c => c.Kind is WfpConditionKind.RemoteAddress or WfpConditionKind.RemoteSubnet)));
            var subnets = core.SelectMany(f => f.Conditions).Where(c => c.Kind == WfpConditionKind.RemoteSubnet).Select(c => c.Address + "/" + c.Number);
            Assert(subnets.Order().SequenceEqual(SingBoxTunConfig.LocalSubnets.Order()));
            // С правилами «напрямую» — прежний открытый выход, локальная сеть в нём уже есть.
            var both = backend.Filters(new[] { IPAddress.Parse("203.0.113.7") }, direct: true, local: true);
            Assert(!both.Any(f => f.Conditions.Any(c => c.Program == backend.TunCoreProgram) && f.Conditions.Any(c => c.Kind == WfpConditionKind.RemoteSubnet)));
            Assert(!SingBoxTunConfig.Create(NetworkAcquire()).ToJsonString().Contains("ip_cidr"));
            // Мимо туннеля: частные подсети — в исключениях маршрутов, вместе с адресами узлов.
            var excluded = config["inbounds"]![0]!["route_exclude_address"]!.AsArray().Select(n => (string?)n).ToArray();
            Assert(SingBoxTunConfig.LocalSubnets.All(excluded.Contains) && excluded.Any(e => e!.EndsWith("/32", StringComparison.Ordinal)));
            Assert(!SingBoxTunConfig.Create(NetworkAcquire())["inbounds"]![0]!["route_exclude_address"]!.AsArray().Any(n => SingBoxTunConfig.LocalSubnets.Contains((string?)n)));
            // Защита открывает подсети всем программам, но раньше запрещает DNS мимо туннеля и чужим — адреса узлов.
            var peer = IPAddress.Parse("192.168.56.10");
            foreach (var v6 in new[] { false, true })
            {
                var filters = backend.Filters(new[] { peer }, local: true).Where(f => f.V6 == v6 && !f.Packet).ToArray();
                var lan = filters.Single(f => f.Permit && f.Conditions.All(c => c.Kind == WfpConditionKind.RemoteSubnet));
                Assert(lan.Conditions.Length == WfpProtectionBackend.LocalSubnets(v6).Count() + WfpProtectionBackend.Discovery(v6).Count());
                // Обнаружение устройств: мультикаст (и широковещание в IPv4) открыт программам вместе с подсетями.
                Assert(lan.Conditions.Any(c => c.Address!.ToString() == (v6 ? "ff00::" : "224.0.0.0")));
                var dns = filters.Single(f => !f.Permit && f.Conditions.Any(c => c.Kind == WfpConditionKind.RemotePort && c.Number == 53));
                Assert(dns.Weight > lan.Weight && dns.Weight < filters.Single(f => f.Name.Contains("DNS туннеля")).Weight);
                // Ядрам DNS к роутеру по-прежнему открыт (DirectDns): их разрешение выше запрета.
                Assert(filters.Where(f => f.Permit && f.Conditions.Any(c => c.Kind == WfpConditionKind.Application)).All(f => f.Weight > dns.Weight));
                if (!v6) Assert(filters.Single(f => !f.Permit && f.Conditions.Any(c => c.Kind == WfpConditionKind.RemoteAddress && c.Address!.Equals(peer))).Weight > lan.Weight);
            }
            Assert(!backend.Filters(new[] { peer }).Any(f => f.Permit && f.Conditions.Length > 0 && f.Conditions.All(c => c.Kind == WfpConditionKind.RemoteSubnet)));
            Assert(backend.Filters(Enumerable.Range(1, 16).Select(i => IPAddress.Parse("203.0.113." + i)).ToArray(), direct: true, local: true).Count <= WfpReinjection.First);
            // Ни CGNAT, ни публичных подсетей в списке нет.
            Assert(!SingBoxTunConfig.LocalSubnets.Any(s => s.StartsWith("100.") || s.StartsWith("0.")));
        });
        await check("Local network flag travels the lease end to end and is refused on release", () => Sync(() =>
        {
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, Array.Empty<string>(), LocalNetwork: true)));
            var lease = NetworkHelperContracts.Validate(NetworkAcquire() with { LocalNetwork = true });
            Assert(lease.LocalNetwork && SingBoxTunnelProcess.RequestFor(lease).LocalNetwork);
            Assert(!DohResolver.IsPrivate(IPAddress.Parse("8.8.8.8")));
        }));
    }
}

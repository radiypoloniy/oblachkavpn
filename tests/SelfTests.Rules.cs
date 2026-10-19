using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // Правила сайтов и программ в системном режиме: от окна через помощника до ядра туннеля.
    private static async Task RuleChecksAsync(Func<string, Func<Task>, Task> check)
    {
        SiteRule[] sites = [RoutingPlan.Create("ipify.org", SiteAction.Direct), RoutingPlan.Create("api.example.com", SiteAction.Tunnel), RoutingPlan.Create("example.org", SiteAction.Block)];
        ProgramRule[] programs = [ProgramRoutingPlan.Create("curl.exe", SiteAction.Direct), ProgramRoutingPlan.Create("game.exe", SiteAction.Block)];
        NetworkLeaseRequest Lease(SiteRule[]? s = null, ProgramRule[]? p = null) =>
            new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"], s, p);

        await check("Without rules the tunnel configuration is unchanged", () =>
        {
            var config = SingBoxTunConfig.Create(Lease());
            Assert(((JsonArray)config["route"]!["rules"]!).Count == 1 && ((JsonArray)config["outbounds"]!).Count == 1);
            return Task.CompletedTask;
        });
        await check("Process lookup runs only with program rules; the tunnel stack is gvisor with MTU 9000", () =>
        {
            Assert(!SingBoxTunConfig.Create(Lease(sites))["route"]!["find_process"]!.GetValue<bool>());
            Assert(SingBoxTunConfig.Create(Lease(sites, programs))["route"]!["find_process"]!.GetValue<bool>());
            var tun = SingBoxTunConfig.Create(Lease())["inbounds"]![0]!;
            Assert(tun["stack"]!.GetValue<string>() == "gvisor" && tun["mtu"]!.GetValue<int>() == 9000);
            return Task.CompletedTask;
        });
        await check("The tunnel gets IPv6 only where the physical network has global IPv6", () =>
        {
            var both = SingBoxTunConfig.Create(Lease(), ipv6: true)["inbounds"]![0]!["address"]!.AsArray();
            var four = SingBoxTunConfig.Create(Lease(), ipv6: false)["inbounds"]![0]!["address"]!.AsArray();
            Assert(both.Count == 2 && four.Count == 1 && four[0]!.GetValue<string>().StartsWith(SingBoxTunConfig.TunHost4, StringComparison.Ordinal));
            Assert(PhysicalNetwork.IsGlobal(IPAddress.Parse("2001:db8::1")) && !PhysicalNetwork.IsGlobal(IPAddress.Parse("fd17::1")) &&
                !PhysicalNetwork.IsGlobal(IPAddress.Parse("fe80::1")));
            return Task.CompletedTask;
        });
        await check("Tunnel rules go DNS, sniff, programs, then sites from the most specific, and sing-box accepts them", async () =>
        {
            var config = SingBoxTunConfig.Create(Lease(sites, programs));
            var rules = ((JsonArray)config["route"]!["rules"]!).Select(r => r!.AsObject()).ToArray();
            Assert(rules[0]["action"]!.GetValue<string>() == "hijack-dns" && rules[1]["action"]!.GetValue<string>() == "sniff");
            Assert(rules[2]["process_name"]![0]!.GetValue<string>() == "curl.exe" && rules[2]["outbound"]!.GetValue<string>() == SingBoxTunConfig.UserDirect);
            Assert(rules[3]["process_name"]![0]!.GetValue<string>() == "game.exe" && rules[3]["action"]!.GetValue<string>() == "reject");
            // Длинный домен раньше: иначе правило родительского домена перехватило бы его.
            Assert(rules[4]["domain_suffix"]![0]!.GetValue<string>() == "api.example.com" && rules[4]["outbound"]!.GetValue<string>() == "local-core");
            Assert(rules.Skip(5).All(r => r.ContainsKey("domain_suffix")) && rules.Length == 7);
            Assert(((JsonArray)config["outbounds"]!).Any(o => o!["tag"]!.GetValue<string>() == SingBoxTunConfig.UserDirect));
            // Без правил «напрямую» выхода direct в ядре туннеля нет вовсе.
            var strict = SingBoxTunConfig.Create(Lease([RoutingPlan.Create("example.org", SiteAction.Block)]));
            Assert(((JsonArray)strict["outbounds"]!).Count == 1);
            await CoreConfigurationCheck.ValidateTunAsync(Lease(sites, programs), CancellationToken.None);
        });
        await check("The helper accepts rules only as validated data and never with a release", () =>
        {
            var json = JsonSerializer.Serialize(Lease(sites, programs), new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            Assert(!json.Contains("Label", StringComparison.Ordinal));
            var parsed = NetworkHelperContracts.Parse(json);
            Assert(parsed.SiteRules.Count == 3 && parsed.ProgramRules.Count == 2 && parsed.HasDirect);
            // Тот же запрос через канал помощника: правила проходят его разбор и лимит размера.
            var request = new HelperRequest(1, Guid.NewGuid(), HelperOperation.Acquire, Lease(sites, programs));
            using (var stream = new MemoryStream())
            {
                HelperProtocol.WriteAsync(stream, request, CancellationToken.None).GetAwaiter().GetResult();
                stream.Position = 0;
                var back = HelperProtocol.ReadAsync<HelperRequest>(stream, CancellationToken.None).GetAwaiter().GetResult();
                HelperProtocol.Validate(back);
                Assert(back.Lease!.Sites!.Length == 3 && back.Lease.Programs!.Length == 2);
            }
            // Полный набор — по 100 правил — помещается в сообщение помощника.
            var many = Lease(Enumerable.Range(0, 100).Select(i => RoutingPlan.Create("site" + i + ".example-domain.com", SiteAction.Direct)).ToArray(),
                Enumerable.Range(0, 100).Select(i => ProgramRoutingPlan.Create("program-number-" + i + ".exe", SiteAction.Block)).ToArray());
            Assert(JsonSerializer.SerializeToUtf8Bytes(new HelperRequest(1, Guid.NewGuid(), HelperOperation.Acquire, many),
                new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }).Length < HelperProtocol.MaxBytes);
            ExpectUserError(() => NetworkHelperContracts.Validate(Lease([new SiteRule("https://bad/", SiteAction.Direct)])));
            ExpectUserError(() => NetworkHelperContracts.Validate(Lease(p: [new ProgramRule(@"C:\x\steam.exe", SiteAction.Direct)])));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, [], sites)));
            Assert(!NetworkHelperContracts.Validate(Lease([RoutingPlan.Create("example.org", SiteAction.Block)])).HasDirect);
            return Task.CompletedTask;
        });
        await check("WFP opens the tunnel core to direct traffic only when the user asked for direct rules", () =>
        {
            var backend = new WfpProtectionBackend(() => new FakeWfp(), @"C:\app\tools");
            var peers = new[] { IPAddress.Parse("203.0.113.7") };
            bool Open(IReadOnlyList<WfpFilter> filters) => filters.Any(f => f.Permit &&
                f.Conditions.All(c => c.Kind == WfpConditionKind.Application) && f.Conditions.Single().Program == backend.TunCoreProgram);
            Assert(!Open(backend.Filters(peers)) && Open(backend.Filters(peers, direct: true)));
            var direct = backend.Prepare(NetworkHelperContracts.Validate(Lease(sites)))[0];
            var strict = backend.Prepare(NetworkHelperContracts.Validate(Lease([RoutingPlan.Create("example.org", SiteAction.Block)])))[0];
            Assert(direct.After.Contains(" direct=1 ", StringComparison.Ordinal) && !strict.After.Contains("direct", StringComparison.Ordinal));
            var engine = new FakeWfp(); var applied = new WfpProtectionBackend(() => engine, @"C:\app\tools");
            applied.Apply(Guid.NewGuid(), direct);
            Assert(Open(engine.Filters.Values.ToArray()));
            return Task.CompletedTask;
        });
        await check("Rules reach the tunnel core through the session, not through the journal", () =>
        {
            ValidatedNetworkLease? started = null;
            using var tunnel = new WindowsTunnelBackend((lease, _) => { started = lease; return new FakeTunnelProcess(); }, _ => started is not null,
                delay: (_, _) => Task.CompletedTask);
            var lease = NetworkHelperContracts.Validate(Lease(sites, programs));
            var change = tunnel.Prepare(lease)[0];
            Assert(change.After.Contains(" rules=5 ", StringComparison.Ordinal) && !change.After.Contains("ipify", StringComparison.Ordinal));
            Assert(tunnel.Apply(lease.LeaseId, change) == NetworkWriteResult.Changed);
            Assert(started!.SiteRules.Count == 3 && started.ProgramRules.Count == 2);
            return Task.CompletedTask;
        });
    }
}

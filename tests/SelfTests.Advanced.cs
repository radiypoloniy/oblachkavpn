using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // «Для продвинутых»: незаполненный раздел даёт ровно прежнюю конфигурацию; заполненный — то,
    // что задано, и ничего мимо защиты сверх заявленного. Служба проверяет значения заново.
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static async Task AdvancedChecksAsync(Func<string, Func<Task>, Task> check)
    {
        NetworkLeaseRequest Lease(TunnelTuning? tuning) => new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["203.0.113.7"], Tuning: tuning);

        await check("Advanced options validate what a person may type and keep defaults as before", () => Sync(() =>
        {
            Assert(AdvancedOptions.Default.IsDefault && AdvancedOptions.Default.Problem() is null && TunnelTuning.From(AdvancedOptions.Default) is null);
            Assert(AdvancedOptions.CheckMtu(1500) is null && AdvancedOptions.CheckMtu(576) is not null && AdvancedOptions.CheckMtu(65000) is not null);
            Assert(AdvancedOptions.CheckDns("https://dns.adguard-dns.com/dns-query") is null && AdvancedOptions.CheckDns("tls://1.1.1.1") is null && AdvancedOptions.CheckDns("quic://dns.example") is null);
            Assert(AdvancedOptions.CheckDns("http://dns.example/dns-query") is not null && AdvancedOptions.CheckDns("8.8.8.8") is not null && AdvancedOptions.CheckDns("https://user:pw@dns.example/") is not null);
            Assert(AdvancedOptions.CheckSubnets(["10.20.0.0/16", "192.168.77.0/24", "fd00:1::/48"]) is null);
            // Шире /8 — почти весь интернет мимо VPN: не принимается.
            Assert(AdvancedOptions.CheckSubnets(["0.0.0.0/0"]) is not null && AdvancedOptions.CheckSubnets(["10.0.0.0/7"]) is not null && AdvancedOptions.CheckSubnets(["10.0.0.0"]) is not null);
            Assert(AdvancedOptions.CheckFragment("tlshello", "50-100", "5-10") is null && AdvancedOptions.CheckFragment("abc", null, null) is not null);
            Assert(AdvancedOptions.CheckUserAgent("Happ/2.0") is null && AdvancedOptions.CheckUserAgent("Облачка") is not null);
            Assert(AdvancedOptions.CheckProbe("https://intranet.example/health") is null && AdvancedOptions.CheckProbe("http://x") is not null);
            Assert(AdvancedOptions.CheckPort(2080) is null && AdvancedOptions.CheckPort(80) is not null);
            // Своя форма фрагментации поверх провайдерской: незаданное — из неё.
            var shape = (AdvancedOptions.Default with { FragmentLength = "10-20" }).Fragment(new ProviderFragment(true, "1-3", "40-60", "2-4"))!;
            Assert(shape is { Packets: "1-3", Length: "10-20", Interval: "2-4", Enabled: true });
            Assert(AdvancedOptions.Default.Fragment(null) is null);
            Assert(TunnelTuning.Normalize("10.20.1.5/16") == "10.20.0.0/16" && TunnelTuning.Normalize("fd00:1:2:3::9/48") == "fd00:1:2::/48");
        }));

        await check("Tunnel tuning changes only the stack, MTU, DNS server and exclusions it names", () => Sync(() =>
        {
            var plain = SingBoxTunConfig.Create(Lease(null), ipv6: false);
            var tuned = SingBoxTunConfig.Create(Lease(new(TunnelStack.System, 1400, "https://dns.example/custom-query", ["10.20.0.0/16"])), ipv6: false);
            JsonObject Tun(JsonObject c) => (JsonObject)((JsonArray)c["inbounds"]!)[0]!;
            Assert((string?)Tun(plain)["stack"] == "gvisor" && (int?)Tun(plain)["mtu"] == 9000);
            Assert((string?)Tun(tuned)["stack"] == "system" && (int?)Tun(tuned)["mtu"] == 1400);
            Assert(Tun(tuned)["route_exclude_address"]!.ToJsonString().Contains("10.20.0.0/16") && !Tun(plain)["route_exclude_address"]!.ToJsonString().Contains("10.20.0.0/16"));
            var servers = (JsonArray)tuned["dns"]!["servers"]!;
            var remote = (JsonObject)servers.First(s => (string?)s!["tag"] == "remote-dns")!;
            Assert((string?)remote["server"] == "dns.example" && (string?)remote["path"] == "/custom-query" && (string?)remote["domain_resolver"] == "dns-bootstrap");
            Assert(servers.Any(s => (string?)s!["tag"] == "dns-bootstrap" && s!["detour"]!.ToString() == remote["detour"]!.ToString()));
            // DoT по IP — без загрузочного DNS.
            var dot = SingBoxTunConfig.Create(Lease(new(Dns: "tls://9.9.9.9")), ipv6: false);
            Assert(((JsonArray)dot["dns"]!["servers"]!).All(s => (string?)s!["tag"] != "dns-bootstrap") && dot["dns"]!["servers"]!.ToJsonString().Contains("\"type\":\"tls\""));
        }));

        await check("Bypass subnets are opened to programs by protection, and the service rejects bad tuning", () => Sync(() =>
        {
            var backend = new WfpProtectionBackend(() => new FakeWfp(), @"C:\app\tools");
            var change = backend.Prepare(NetworkHelperContracts.Validate(Lease(new(Bypass: ["10.20.1.0/24"]))))[0];
            Assert(change.After.Contains(" bypass=10.20.1.0/24 "));
            var engine = new FakeWfp();
            new WfpProtectionBackend(() => engine, @"C:\app\tools").Apply(Guid.NewGuid(), change);
            var bypass = engine.Filters.Values.Where(f => f.Name.Contains("подсети мимо туннеля")).ToArray();
            Assert(bypass.Length == 1 && bypass[0].Permit && !bypass[0].V6 && bypass[0].Conditions.Single() is { Kind: WfpConditionKind.RemoteSubnet, Number: 24 });
            // Подмена строки журнала на «весь интернет» не проходит.
            var forged = change with { After = change.After.Replace("10.20.1.0/24", "0.0.0.0/0") };
            Assert(Throws<UserError>(() => backend.Apply(Guid.NewGuid(), forged)));
            foreach (var bad in new TunnelTuning[] { new(Mtu: 100), new(Dns: "http://dns.example/"), new(Bypass: ["0.0.0.0/1"]), new((TunnelStack)9) })
                Assert(Throws<UserError>(() => NetworkHelperContracts.Validate(Lease(bad))));
            Assert(Throws<UserError>(() => NetworkHelperContracts.Validate(new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, [], Tuning: new(Mtu: 1500)))));
        }));
    }
}

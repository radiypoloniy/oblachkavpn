using System.Net;

namespace Oblachka;

public static partial class SelfTests
{
    private sealed class FakeProbe : INetworkProbe
    {
        public ProxySettings ProxyValue = new(0, null, null, null);
        public HashSet<int> Open = new();
        public Dictionary<string, string> Env = new();
        public string? WinHttp;
        public List<AdapterInfo> AdapterList = new();
        public HashSet<IPAddress> Answering = new();
        public List<RouteInfo> Routes = new();
        public List<BypassInfo> Bypass = new();
        public List<string> Hosts = new();
        public string? Foreign;
        public ProxySettings Proxy() => ProxyValue;
        public bool Listening(int port) => Open.Contains(port);
        public IReadOnlyDictionary<string, string> EnvironmentProxies() => Env;
        public string? WinHttpProxy() => WinHttp;
        public IReadOnlyList<AdapterInfo> Adapters() => AdapterList;
        public bool AnswersDns(IPAddress server) => Answering.Contains(server);
        public IReadOnlyList<RouteInfo> DefaultRoutes() => Routes;
        public IReadOnlyList<BypassInfo> BypassTools() => Bypass;
        public IReadOnlyList<string> HostsEntries() => Hosts;
        public string? ForeignVpn() => Foreign;
    }

    private sealed class MemoryProxyStore(ProxySettings start) : IProxySettingsStore
    {
        public ProxySettings Value = start;
        public ProxySettings Read() => Value;
        public void Write(ProxySettings settings) => Value = settings;
    }

    private static async Task NetworkDoctorChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Network doctor finds dead proxies, fake DNS, dead routes and DPI tools; leaves working ones alone", () => Sync(() =>
        {
            Assert(NetworkDoctor.LoopbackPorts("127.0.0.1:2080").Single() == 2080);
            Assert(NetworkDoctor.LoopbackPorts("http=127.0.0.1:10809;https=127.0.0.1:10809").Distinct().Single() == 10809);
            Assert(NetworkDoctor.LoopbackPorts("socks5://localhost:1080/").Single() == 1080 && NetworkDoctor.LoopbackPorts("http://127.0.0.1:2081/pac").Single() == 2081);
            Assert(!NetworkDoctor.LoopbackPorts("proxy.corp.example:3128").Any());

            var probe = new FakeProbe
            {
                ProxyValue = new(1, "127.0.0.1:2080", null, "http://127.0.0.1:2081/proxy.pac"),
                Env = { ["HTTP_PROXY"] = "http://127.0.0.1:7890" },
                WinHttp = "127.0.0.1:2080",
                AdapterList = { new("{11111111-1111-1111-1111-111111111111}", "Wi-Fi", true, [IPAddress.Parse("127.0.0.1")]),
                                new("{22222222-2222-2222-2222-222222222222}", "Ethernet", true, [IPAddress.Parse("1.1.1.1")]) },
                Routes = { new("0.0.0.0", "128.0.0.0", "10.0.0.1", 42, false), new("0.0.0.0", "0.0.0.0", "192.168.0.1", 7, true) },
                Bypass = { new("zapret (winws)", true) },
                Hosts = { "1.2.3.4 youtube.com" }
            };
            var found = NetworkDoctor.Diagnose(probe);
            var kinds = found.Select(f => f.Action.Kind).ToArray();
            foreach (var kind in new[] { RepairKind.DeadProxy, RepairKind.DeadPac, RepairKind.DeadEnvProxy, RepairKind.WinHttpProxy, RepairKind.AdapterDns, RepairKind.DeadRoute, RepairKind.StopBypass, RepairKind.FlushDns })
                if (!kinds.Contains(kind)) throw new InvalidOperationException("Not found: " + kind);
            Assert(found.Single(f => f.Action.Kind == RepairKind.AdapterDns).Action.Target!.StartsWith("{1111"));
            Assert(found.Single(f => f.Action.Kind == RepairKind.DeadRoute).Action.Target == "0.0.0.0|128.0.0.0|10.0.0.1|42");
            Assert(found.Any(f => f.Title.Contains("hosts") && f.Action.Kind == RepairKind.None));
            Assert(found.Single(f => f.Action.Kind == RepairKind.DeadProxy).Text.Contains("2080"));

            // Прокси жив, свой DNS отвечает, другой VPN работает — ничего не трогаем, только сообщаем.
            probe.Open.UnionWith([2080, 2081, 7890]); probe.Answering.Add(IPAddress.Parse("127.0.0.1"));
            probe.Routes.Clear(); probe.Bypass.Clear(); probe.Hosts.Clear(); probe.Foreign = "Throne";
            var calm = NetworkDoctor.Diagnose(probe);
            Assert(calm.Count == 1 && calm[0].Action.Kind == RepairKind.None && calm[0].Title.Contains("Throne"));
            Assert(WindowsNetworkProbe.IsPublic(IPAddress.Parse("1.2.3.4")) && !WindowsNetworkProbe.IsPublic(IPAddress.Parse("192.168.31.14")) &&
                   !WindowsNetworkProbe.IsPublic(IPAddress.Parse("0.0.0.0")) && !WindowsNetworkProbe.IsPublic(IPAddress.Parse("127.0.0.1")));
            Assert(NetworkDoctor.FakeDns(IPAddress.Parse("198.18.0.2")) && !NetworkDoctor.FakeDns(IPAddress.Parse("8.8.8.8")));
        }));

        await check("Network repair: local fixes keep the original values and undo them; service commands are narrow", () => Sync(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "oblachka-repair-" + Guid.NewGuid().ToString("N"));
            NetworkRepair.BackupOverride = Path.Combine(folder, "backup.json");
            try
            {
                var original = new ProxySettings(1, "127.0.0.1:2080", "localhost", "http://127.0.0.1:2081/pac");
                var store = new MemoryProxyStore(original);
                NetworkRepair.ApplyLocal(new(RepairKind.DeadProxy), store);
                NetworkRepair.ApplyLocal(new(RepairKind.DeadPac), store);
                Assert(store.Value.ProxyEnable == 0 && store.Value.ProxyServer == "127.0.0.1:2080" && store.Value.AutoConfigUrl is null && NetworkRepair.HasBackup);
                Assert(NetworkRepair.Undo(store) && store.Value == original && !NetworkRepair.HasBackup && !NetworkRepair.Undo(store));
                ExpectUserError(() => NetworkRepair.ApplyLocal(new(RepairKind.WinHttpProxy), store));
            }
            finally { NetworkRepair.BackupOverride = null; if (Directory.Exists(folder)) Directory.Delete(folder, true); }

            new RepairRequest(RepairKind.AdapterDns, "{11111111-1111-1111-1111-111111111111}").Validate();
            new RepairRequest(RepairKind.DeadRoute, "0.0.0.0|128.0.0.0|10.0.0.1|42").Validate();
            new RepairRequest(RepairKind.StopBypass, "GoodbyeDPI").Validate();
            ExpectUserError(() => new RepairRequest(RepairKind.StopBypass, "explorer").Validate());
            ExpectUserError(() => new RepairRequest(RepairKind.AdapterDns, "Wi-Fi & calc").Validate());
            ExpectUserError(() => new RepairRequest(RepairKind.DeadProxy, null).Validate());
            ExpectUserError(() => HelperProtocol.Validate(new HelperRequest(1, Guid.NewGuid(), HelperOperation.Repair, null, null)));
            ExpectUserError(() => HelperProtocol.Validate(new HelperRequest(1, Guid.NewGuid(), HelperOperation.Heartbeat, null, new(RepairKind.WinHttpProxy, null))));
            HelperProtocol.Validate(new HelperRequest(1, Guid.NewGuid(), HelperOperation.Repair, null, new(RepairKind.WinHttpProxy, null)));
            // Служба сама проверяет поломку: на исправной «машине» ничего не делает.
            var healthy = new FakeProbe();
            Assert(!NetworkRepairBackend.Apply(new(RepairKind.WinHttpProxy, null), healthy));
            Assert(!NetworkRepairBackend.Apply(new(RepairKind.DeadRoute, "0.0.0.0|128.0.0.0|10.0.0.1|42"), healthy));
            Assert(!NetworkRepairBackend.Apply(new(RepairKind.AdapterDns, "{11111111-1111-1111-1111-111111111111}"), healthy));
        }));
    }
}

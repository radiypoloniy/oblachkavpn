using System.Net;
using System.Runtime.InteropServices;

namespace Oblachka;

public static partial class SelfTests
{
    // Подменный движок WFP: сеть компьютера не меняется, зато видны транзакции и состав.
    private sealed class FakeWfp : IWfpEngine
    {
        public readonly List<string> Log = new();
        public readonly Dictionary<Guid, WfpFilter> Filters = new();
        public bool Sublayer, Provider, InTransaction, FailOnFilter;
        private (bool, bool, Dictionary<Guid, WfpFilter>)? saved;
        public void Begin() { InTransaction = true; saved = (Sublayer, Provider, new(Filters)); Log.Add("begin"); }
        public void Commit() { InTransaction = false; saved = null; Log.Add("commit"); }
        public void Abort()
        {
            Log.Add("abort"); InTransaction = false;
            if (saved is var (s, p, f)) { Sublayer = s; Provider = p; Filters.Clear(); foreach (var kv in f) Filters[kv.Key] = kv.Value; }
        }
        public bool SublayerExists(Guid key) => Sublayer;
        public void AddProvider(Guid key, string name) { Provider = true; Log.Add("provider+"); }
        public void AddSublayer(Guid key, Guid provider, string name, ushort weight)
        { Assert(weight == ushort.MaxValue && InTransaction); Sublayer = true; Log.Add("sublayer+"); }
        public void AddFilter(WfpFilter filter, Guid provider, Guid sublayer)
        {
            Assert(InTransaction);
            if (FailOnFilter && Filters.Count == 3) throw new IOException("fixture");
            Filters.Add(filter.Key, filter); Log.Add("filter+");
        }
        public bool DeleteFilter(Guid key) { Assert(InTransaction); return Filters.Remove(key); }
        public bool DeleteSublayer(Guid key) { Assert(Filters.Count == 0); var had = Sublayer; Sublayer = false; Log.Add("sublayer-"); return had; }
        public bool DeleteProvider(Guid key) { var had = Provider; Provider = false; return had; }
        public void Dispose() => Log.Add("close");
    }

    private static async Task WfpChecksAsync(Func<string, Func<Task>, Task> check)
    {
        NetworkLeaseRequest Request(Guid id, params string[] peers) => new(1, id, NetworkLeaseAction.Acquire, 10808, peers);

        await check("WFP structures match the x64 layout of fwpmtypes.h", () =>
        {
            // Ошибка раскладки проявилась бы только отказом WFP на живой системе.
            Assert(Marshal.SizeOf<WfpNative.Filter>() == 200 && Marshal.SizeOf<WfpNative.Condition>() == 40);
            Assert(Marshal.SizeOf<WfpNative.Session>() == 72 && Marshal.SizeOf<WfpNative.Sublayer>() == 72);
            Assert(Marshal.SizeOf<WfpNative.Provider>() == 64 && Marshal.SizeOf<WfpNative.Value>() == 16);
            Assert(Marshal.OffsetOf<WfpNative.Filter>(nameof(WfpNative.Filter.Context)) == 152);
            Assert(Marshal.OffsetOf<WfpNative.Filter>(nameof(WfpNative.Filter.EffectiveWeight)) == 184);
            return Task.CompletedTask;
        });
        await check("WFP protection permits only the tunnel, loopback, DHCP and the cores to the peers, and blocks the rest", () =>
        {
            var backend = new WfpProtectionBackend(() => new FakeWfp(), @"C:\app\tools");
            var filters = backend.Filters(new[] { IPAddress.Parse("203.0.113.7"), IPAddress.Parse("2001:db8::7") });
            foreach (var v6 in new[] { false, true })
            {
                var family = filters.Where(f => f.V6 == v6).ToArray();
                var block = family.Single(f => !f.Permit);
                // Запрет без условий и с наименьшим весом: ниже всех разрешений подслоя.
                Assert(block.Conditions.Length == 0 && block.Weight == 0 && family.Where(f => f.Permit).All(f => f.Weight > 0));
                // Адрес узла открыт только программам ядер — по одному фильтру на ядро.
                var peers = family.Where(f => f.Conditions.Any(c => c.Kind == WfpConditionKind.Application)).ToArray();
                Assert(peers.Length == 2 && peers.All(f => f.Conditions.Any(c => c.Kind == WfpConditionKind.RemoteAddress &&
                    (c.Address!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) == v6)));
                Assert(peers.Select(f => f.Conditions.Single(c => c.Kind == WfpConditionKind.Application).Program)
                    .SequenceEqual(new[] { @"C:\app\tools\xray.exe", @"C:\app\tools\sing-box.exe" }));
                // Разрешений без условий быть не может: такое открыло бы всё.
                Assert(family.Where(f => f.Permit).All(f => f.Conditions.Length > 0));
                Assert(family.Any(f => f.Conditions.Any(c => c.Kind == WfpConditionKind.Loopback)));
            }
            Assert(filters.Select(f => f.Key).Distinct().Count() == filters.Count);
            Assert(filters.Select((f, i) => f.Key == WfpProtectionBackend.FilterKey(i)).All(x => x));
            return Task.CompletedTask;
        });
        await check("WFP protection is applied in one transaction and removed completely, leftovers first", () =>
        {
            var engine = new FakeWfp { Sublayer = true, Provider = true };
            engine.Filters[WfpProtectionBackend.FilterKey(5)] = new(WfpProtectionBackend.FilterKey(5), "leftover", false, 0, false, []);
            var backend = new WfpProtectionBackend(() => engine);
            var id = Guid.NewGuid();
            var change = backend.Prepare(NetworkHelperContracts.Validate(Request(id, "203.0.113.7")))[0];
            Assert(change.Kind == NetworkResourceKind.WfpProtection && change.Before is null);
            Assert(backend.Apply(id, change) == NetworkWriteResult.Changed);
            // Остаток снят своей транзакцией до новой, новая — целиком одной.
            Assert(engine.Log.IndexOf("sublayer-") < engine.Log.LastIndexOf("begin") && engine.Log.Count(l => l == "commit") == 2);
            Assert(engine.Sublayer && engine.Filters.Count == backend.Filters(new[] { IPAddress.Parse("203.0.113.7") }).Count);
            Assert(backend.Restore(id, change with { State = NetworkChangeState.Applied }) == NetworkWriteResult.Changed);
            Assert(!engine.Sublayer && !engine.Provider && engine.Filters.Count == 0);
            Assert(backend.Restore(id, change) == NetworkWriteResult.Unchanged);
            ExpectUserError(() => backend.Apply(id, change with { Kind = NetworkResourceKind.KillSwitch }));
            return Task.CompletedTask;
        });
        await check("A failed WFP transaction leaves nothing behind", () =>
        {
            var engine = new FakeWfp { FailOnFilter = true };
            var backend = new WfpProtectionBackend(() => engine);
            var id = Guid.NewGuid();
            var change = backend.Prepare(NetworkHelperContracts.Validate(Request(id, "203.0.113.7")))[0];
            var failed = false;
            try { backend.Apply(id, change); } catch (IOException) { failed = true; }
            Assert(failed && engine.Log.Contains("abort") && !engine.Sublayer && engine.Filters.Count == 0);
            return Task.CompletedTask;
        });
        await check("A real-machine report hides the machine's own addresses and resolvers", () =>
        {
            Assert(SystemTunnelCheckCommand.Mask("исходный выход (до туннеля): 198.51.100.4", "198.51.100.4", null) == "исходный выход (до туннеля): <свой IPv4>");
            Assert(SystemTunnelCheckCommand.Mask("IPv6: 2001:db8::5", null, "2001:db8::5") == "IPv6: <свой IPv6>");
            Assert(SystemTunnelCheckCommand.Mask("резолверы до туннеля: Ethernet: 192.0.2.53", null, null) == "резолверы до туннеля: скрыто");
            Assert(SystemTunnelCheckCommand.Mask("выходной IP: 203.0.113.9", "198.51.100.4", null) == "выходной IP: 203.0.113.9");
            return Task.CompletedTask;
        });
        await check("The window notices a session the helper did not release", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "oblachka-peek-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Assert(NetworkSessionJournal.PeekState(path) is null);
                File.WriteAllText(path, "{\"Version\":1,\"State\":\"Applied\"}");
                Assert(NetworkSessionJournal.PeekState(path) == NetworkSessionState.Applied);
                File.WriteAllText(path, "{\"Version\":1,\"State\":\"Released\"}");
                Assert(NetworkSessionJournal.PeekState(path) == NetworkSessionState.Released);
                // Непрочитанный журнал — тоже повод восстановить сеть, а не молчать.
                File.WriteAllText(path, "{oops");
                Assert(NetworkSessionJournal.PeekState(path) == NetworkSessionState.RecoveryRequired);
            }
            finally { File.Delete(path); }
            return Task.CompletedTask;
        });
        await check("System mode prefers WFP protection over the firewall and journals it first", () =>
        {
            var firewall = new FakeFirewall();
            using var tunnel = new WindowsTunnelBackend((_, _) => new FakeTunnelProcess(), _ => false, delay: (_, _) => Task.CompletedTask);
            using var backend = new SystemNetworkBackend(tunnel, new KillSwitchBackend(firewall), new WfpProtectionBackend(() => new FakeWfp()));
            var lease = NetworkHelperContracts.Validate(Request(Guid.NewGuid(), "203.0.113.7"));
            var changes = backend.Prepare(lease);
            Assert(changes[0].Kind == NetworkResourceKind.WfpProtection && changes.All(c => c.Kind != NetworkResourceKind.KillSwitch));
            var record = new NetworkSessionRecord(1, lease.LeaseId, NetworkSessionState.Prepare, changes.ToArray());
            NetworkSessionValidation.Validate(record);
            Assert(SystemNetworkBackend.Blocks(record));
            // Брандмауэр при этом не трогается вовсе.
            Assert(firewall.Log.Count == 0);
            return Task.CompletedTask;
        });
    }
}

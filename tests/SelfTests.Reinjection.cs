namespace Oblachka;

public static partial class SelfTests
{
    // zapret под «Весь компьютер»: пакеты, повторно отправленные WinDivert, Windows видит как
    // соединения от System. Разрешения — только для System, только на порты перехвата стратегии.
    private static async Task ReinjectionChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("zapret permits under protection follow the strategy's capture ports and fit the reserved slots", () => Sync(() =>
        {
            var ports = WfpReinjection.Ports(new[] { "--wf-tcp=80,443,2053,2083,2087,2096,8443,12", "--wf-udp=443,19294-19344,50000-50100,12", "--filter-tcp=443" });
            // Семь диапазонов на семейство: соседние порты сливаются, ни один порт перехвата не теряется.
            Assert(ports.Count <= WfpReinjection.PerFamily);
            foreach (var (protocol, port) in new[] { (6, 80), (6, 443), (6, 2053), (6, 2096), (6, 8443), (6, 12), (17, 443), (17, 19300), (17, 50050), (17, 12) })
                Assert(ports.Any(r => r.Protocol == protocol && r.Low <= port && port <= r.High));
            Assert(!ports.Any(r => r.Protocol == 6 && r.Low <= 445 && 445 <= r.High));
            Assert(WfpReinjection.Ports(new[] { "--wf-tcp=0,70000,abc,500-400" }).Count == 0);

            var filters = WfpReinjection.Filters(ports);
            Assert(filters.Count == ports.Count * 2 && filters.All(f => f.Permit && !f.Packet));
            Assert(filters.All(f => f.Conditions.Any(c => c.Kind == WfpConditionKind.KernelProcess) && f.Conditions.Any(c => c.Kind == WfpConditionKind.Protocol)));
            var keys = Enumerable.Range(WfpReinjection.First, WfpReinjection.Slots).Select(WfpProtectionBackend.FilterKey).ToHashSet();
            Assert(filters.All(f => keys.Contains(f.Key)));
            // Защита со всеми 16 узлами не заходит на номера разрешений zapret.
            var peers = Enumerable.Range(1, 16).Select(i => System.Net.IPAddress.Parse("203.0.113." + i)).ToArray();
            var protection = new WfpProtectionBackend(() => new FakeWfp(), @"C:\app\tools").Filters(peers, direct: true);
            Assert(protection.All(f => !keys.Contains(f.Key)));
        }));
        await check("zapret permits are added only inside the protection sublayer and removed with winws", () => Sync(() =>
        {
            var engine = new FakeWfp();
            var ports = WfpReinjection.Ports(new[] { "--wf-tcp=443" });
            Assert(!WfpReinjection.Allow(() => engine, ports) && engine.Filters.Count == 0);
            engine.Sublayer = true;
            Assert(WfpReinjection.Allow(() => engine, ports) && engine.Filters.Count == 2);
            Assert(WfpReinjection.Allow(() => engine, ports) && engine.Filters.Count == 2);
            WfpReinjection.Revoke(() => engine);
            Assert(engine.Filters.Count == 0);
            WfpReinjection.Forget();
        }));
    }
}

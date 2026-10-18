namespace Oblachka;

public static partial class SelfTests
{
    // Подменный брандмауэр: сеть компьютера не меняется, зато виден порядок операций.
    // Порядок здесь и есть главное требование — захват трафика показал, что утечки лежат
    // на краях окна, то есть до поднятия туннеля и после его снятия.
    private sealed class FakeFirewall : IFirewallPolicy
    {
        public readonly Dictionary<int, FirewallAction> Outbound = new()
        {
            [1] = FirewallAction.Allow, [2] = FirewallAction.Allow, [4] = FirewallAction.Allow
        };
        public readonly Dictionary<string, FirewallRule> Rules = new(StringComparer.Ordinal);
        public readonly List<string> Log = new();
        public bool Enabled = true;
        public bool RefuseRemoval;

        public bool IsEnabled(int profile) => Enabled;
        public FirewallAction OutboundAction(int profile) => Outbound[profile];

        public void SetOutboundAction(int profile, FirewallAction action)
        {
            Outbound[profile] = action;
            Log.Add("outbound " + profile + "=" + action);
        }

        public void AddRule(FirewallRule rule)
        {
            Rules[rule.Name] = rule;
            Log.Add("rule+ " + rule.Name + " " + rule.Action);
        }

        public bool RuleExists(string name) => Rules.ContainsKey(name);

        public void RemoveRule(string name)
        {
            if (RefuseRemoval) return;
            if (Rules.Remove(name)) Log.Add("rule- " + name);
        }
    }

    private static async Task KillSwitchChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        var peers = new[] { "203.0.113.7" };
        NetworkLeaseRequest Request(Guid id) => new(1, id, NetworkLeaseAction.Acquire, 10808, peers);
        static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

        await check("Blocking traffic outside the tunnel comes before the tunnel itself", () => Sync(() =>
        {
            var lease = NetworkHelperContracts.Validate(Request(Guid.NewGuid()));
            var firewall = new FakeFirewall();
            using var tunnel = new WindowsTunnelBackend((_, _) => new FakeTunnelProcess(), _ => false, delay: NoDelay);
            using var backend = new SystemNetworkBackend(tunnel, new KillSwitchBackend(firewall));
            var changes = backend.Prepare(lease);
            Assert(changes.Count == 2);
            // Сессия применяет по списку и снимает в обратном порядке, поэтому первая
            // запись встаёт до туннеля и снимается после него.
            Assert(changes[0].Kind == NetworkResourceKind.KillSwitch);
            Assert(changes[1].Kind == NetworkResourceKind.TunnelInterface);
            Assert(SystemNetworkBackend.Blocks(new NetworkSessionRecord(1, lease.LeaseId, NetworkSessionState.Prepare, changes.ToArray())));
            // Журнал обязан принимать такой план: порядок видов в нём не убывает.
            NetworkSessionValidation.Validate(new NetworkSessionRecord(1, lease.LeaseId, NetworkSessionState.Prepare, changes.ToArray()));
        }));
        await check("Allow rules exist before the block takes effect, and name the tunnel's own address", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var firewall = new FakeFirewall();
            var kill = new KillSwitchBackend(firewall);
            var change = kill.Prepare(NetworkHelperContracts.Validate(Request(id)))[0];
            Assert(kill.Apply(id, change) == NetworkWriteResult.Changed);
            // Иначе осталось бы окно, в котором наружу не проходит и сам туннель.
            var firstBlock = firewall.Log.FindIndex(l => l.StartsWith("outbound", StringComparison.Ordinal));
            Assert(firewall.Log.Take(firstBlock).Count(l => l.StartsWith("rule+", StringComparison.Ordinal)) == KillSwitchBackend.RuleNames.Count);
            Assert(firewall.Outbound.Values.All(a => a == FirewallAction.Block));
            // Адрес туннеля берётся из конфигурации ядра: расхождение закрыло бы туннель наглухо.
            var tunnelRule = firewall.Rules[KillSwitchBackend.TunnelRule];
            Assert(tunnelRule.LocalAddresses!.Contains(SingBoxTunConfig.TunHost4, StringComparison.Ordinal));
            Assert(tunnelRule.LocalAddresses!.Contains(SingBoxTunConfig.TunHost6, StringComparison.Ordinal));
            // Адрес узла открыт только программам ядер из папки сборки, и больше никому:
            // разрешение без программы пустило бы к этому адресу мимо туннеля что угодно.
            var xray = firewall.Rules[KillSwitchBackend.PeerXrayRule];
            var singBox = firewall.Rules[KillSwitchBackend.PeerSingBoxRule];
            Assert(xray.RemoteAddresses == "203.0.113.7" && singBox.RemoteAddresses == "203.0.113.7");
            Assert(xray.Program == kill.XrayProgram && singBox.Program == kill.SingBoxProgram);
            var tunCore = firewall.Rules[KillSwitchBackend.PeerTunCoreRule];
            Assert(tunCore.RemoteAddresses == "203.0.113.7" && tunCore.Program == kill.TunCoreProgram && tunCore.Program!.EndsWith(TunCore.FileName, StringComparison.Ordinal));
            Assert(Path.IsPathFullyQualified(xray.Program!) && xray.Program!.EndsWith(Path.Combine("tools", "xray.exe"), StringComparison.Ordinal));
            Assert(!firewall.Rules.ContainsKey(KillSwitchBackend.PeerRule));
            Assert(firewall.Rules.Values.Where(r => r.Action == FirewallAction.Allow && r.Program is null)
                .All(r => r.RemoteAddresses is null || !r.RemoteAddresses.Contains("203.0.113.7", StringComparison.Ordinal)));
            // DNS запрещается именно запретом: встроенные разрешения Windows иначе
            // выпускают его на физический адаптер, что и показал захват на стенде.
            // Резолверу туннеля нужно собственное разрешение: запрет по умолчанию
            // накрывает и его, и тогда имена не разрешаются вовсе.
            var tunnelDns = firewall.Rules[KillSwitchBackend.TunnelDnsRule];
            Assert(tunnelDns.Action == FirewallAction.Allow);
            Assert(tunnelDns.RemoteAddresses!.Contains(SingBoxTunConfig.TunResolver4, StringComparison.Ordinal));
            foreach (var name in new[] { KillSwitchBackend.DnsRule, KillSwitchBackend.DnsTcpRule })
            {
                var dns = firewall.Rules[name];
                Assert(dns.Action == FirewallAction.Block && dns.RemotePorts == "53");
                // Решение принимается по адресу получателя: запрет по адресу отправителя
                // на стенде погасил и запросы к резолверу самого туннеля.
                Assert(dns.LocalAddresses is null && dns.RemoteAddresses is not null);
                // Резолвер туннеля из запрета исключён, иначе имена не разрешались бы вовсе.
                Assert(!dns.RemoteAddresses!.Contains(SingBoxTunConfig.TunResolver4, StringComparison.Ordinal));
                Assert(!dns.RemoteAddresses!.Contains(SingBoxTunConfig.TunResolver6, StringComparison.Ordinal));
            }
        }));
        await check("Someone else's outbound setting is a conflict, not something to overwrite", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var firewall = new FakeFirewall();
            var kill = new KillSwitchBackend(firewall);
            var change = kill.Prepare(NetworkHelperContracts.Validate(Request(id)))[0];
            firewall.Outbound[2] = FirewallAction.Block;
            Assert(kill.Apply(id, change) == NetworkWriteResult.Conflict);
            Assert(firewall.Rules.Count == 0);
            // Чужая запись нашей блокировкой не распоряжается.
            var foreign = change with { Kind = NetworkResourceKind.TunnelInterface };
            ExpectUserError(() => kill.Apply(id, foreign));
            ExpectUserError(() => kill.Restore(id, foreign));
        }));
        await check("Release lifts the block after the tunnel is already down", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var firewall = new FakeFirewall();
            var up = false;
            var core = new FakeTunnelProcess();
            using var tunnel = new WindowsTunnelBackend((_, _) => { up = true; firewall.Log.Add("core+"); return core; },
                _ => up && core.IsRunning, delay: NoDelay);
            using var backend = new SystemNetworkBackend(tunnel, new KillSwitchBackend(firewall));
            var journal = new NetworkSessionJournal(Path.Combine(root, "kill-switch.json"));
            var session = new NetworkSession(journal, backend);

            var applied = session.Acquire(Request(id));
            Assert(applied.State == NetworkSessionState.Applied && SystemNetworkBackend.Blocks(applied));
            // Блокировка встала до запуска ядра.
            Assert(firewall.Log.IndexOf("core+") > firewall.Log.FindLastIndex(l => l.StartsWith("outbound", StringComparison.Ordinal)));

            up = false;
            var released = session.Release(id);
            Assert(released.State == NetworkSessionState.Released);
            Assert(released.Changes.All(c => c.State == NetworkChangeState.Restored));
            Assert(firewall.Outbound.Values.All(a => a == FirewallAction.Allow));
            Assert(firewall.Rules.Count == 0 && core.Disposals == 1);
            // Исходящее действие возвращается раньше снятия разрешений: если бы снятие
            // сорвалось посередине, компьютер остался бы без сети, а пользователь уже
            // попросил выключить режим.
            var restored = firewall.Log.FindIndex(l => l == "outbound 1=Allow");
            Assert(restored >= 0 && restored < firewall.Log.FindIndex(l => l.StartsWith("rule-", StringComparison.Ordinal)));
        }));
        await check("A block that never got applied is cleaned up without touching the setting", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var firewall = new FakeFirewall();
            var kill = new KillSwitchBackend(firewall);
            var change = kill.Prepare(NetworkHelperContracts.Validate(Request(id)))[0];
            // Остаток правила от потерянного журнала: убрать его нужно, настройку — нет.
            firewall.AddRule(new FirewallRule(KillSwitchBackend.PeerRule));
            Assert(kill.Restore(id, change with { State = NetworkChangeState.Applied }) == NetworkWriteResult.Unchanged);
            Assert(firewall.Rules.Count == 0);
            Assert(firewall.Outbound.Values.All(a => a == FirewallAction.Allow));
        }));
        await check("Outbound setting changed under us during the session is a conflict", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var firewall = new FakeFirewall();
            var kill = new KillSwitchBackend(firewall);
            var change = kill.Prepare(NetworkHelperContracts.Validate(Request(id)))[0];
            Assert(kill.Apply(id, change) == NetworkWriteResult.Changed);
            firewall.Outbound[4] = FirewallAction.Allow;
            Assert(kill.Restore(id, change with { State = NetworkChangeState.Applied }) == NetworkWriteResult.Conflict);
        }));
        await check("A rule that refuses to go away is reported, not assumed gone", () => Sync(() =>
        {
            // Оставшееся разрешение — это дыра, которая переживёт сессию.
            var id = Guid.NewGuid();
            var firewall = new FakeFirewall();
            var kill = new KillSwitchBackend(firewall);
            var change = kill.Prepare(NetworkHelperContracts.Validate(Request(id)))[0];
            Assert(kill.Apply(id, change) == NetworkWriteResult.Changed);
            firewall.RefuseRemoval = true;
            var reported = false;
            try { kill.Restore(id, change with { State = NetworkChangeState.Applied }); } catch (IOException) { reported = true; }
            Assert(reported);
            // Исходящее действие при этом уже возвращено: пользователь без сети не остаётся.
            Assert(firewall.Outbound.Values.All(a => a == FirewallAction.Allow));
        }));
        await check("Disabled firewall gives the mode without blocking, and the record says so", () => Sync(() =>
        {
            var firewall = new FakeFirewall { Enabled = false };
            var kill = new KillSwitchBackend(firewall);
            Assert(!kill.IsAvailable);
            using var tunnel = new WindowsTunnelBackend((_, _) => new FakeTunnelProcess(), _ => false, delay: NoDelay);
            using var backend = new SystemNetworkBackend(tunnel, kill);
            var lease = NetworkHelperContracts.Validate(Request(Guid.NewGuid()));
            var changes = backend.Prepare(lease);
            // Обещать защиту, которой нет, нельзя: изменения просто не появляется.
            Assert(changes.Count == 1 && changes[0].Kind == NetworkResourceKind.TunnelInterface);
            Assert(!SystemNetworkBackend.Blocks(new NetworkSessionRecord(1, lease.LeaseId, NetworkSessionState.Prepare, changes.ToArray())));
            // Прямой вызов при выключенном брандмауэре обязан отказать, а не притворяться.
            ExpectUserError(() => kill.Prepare(lease));
        }));
        await check("Recovery lifts a block left by an earlier session", () => Sync(() =>
        {
            // Журнал мог остаться после краха вместе с запретом исходящего трафика.
            // Это худшее состояние: компьютер почти без сети, и снять его обязан запуск.
            var id = Guid.NewGuid();
            var firewall = new FakeFirewall();
            var up = false;
            var core = new FakeTunnelProcess();
            using var tunnel = new WindowsTunnelBackend((_, _) => { up = true; return core; }, _ => up, delay: NoDelay);
            using var backend = new SystemNetworkBackend(tunnel, new KillSwitchBackend(firewall));
            var journal = new NetworkSessionJournal(Path.Combine(root, "kill-switch-recovery.json"));
            var session = new NetworkSession(journal, backend);
            session.Acquire(Request(id));
            up = false;      // ядро убито вместе с владельцем, запрет остался
            Assert(firewall.Outbound.Values.All(a => a == FirewallAction.Block));
            var recovered = session.Recover();
            Assert(recovered!.State == NetworkSessionState.Released);
            Assert(firewall.Outbound.Values.All(a => a == FirewallAction.Allow) && firewall.Rules.Count == 0);
        }));
        await Task.CompletedTask;
    }
}

namespace Oblachka;

public static partial class SelfTests
{
    // Реальный сетевой backend проверяется на подменном ядре и подменном списке
    // интерфейсов: сеть компьютера при этом не меняется. Сценарии взяты из того, что
    // показала проверка на VM.
    private sealed class FakeTunnelProcess : ITunnelProcess
    {
        public bool IsRunning { get; private set; } = true;
        public int Disposals;
        public void Dispose() { Disposals++; IsRunning = false; }
    }

    // Ядро, которое не умирает по Dispose: настоящий отказ остановки.
    private sealed class StubbornTunnelProcess : ITunnelProcess
    {
        public bool IsRunning => true;
        public void Dispose() { }
    }

    private static async Task TunnelBackendChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        var peers = new[] { "203.0.113.7" };
        NetworkLeaseRequest Request(Guid id) => new(1, id, NetworkLeaseAction.Acquire, 10808, peers);
        static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

        await check("Tunnel session owns one change: the core process with its interface", () => Sync(() =>
        {
            var lease = NetworkHelperContracts.Validate(Request(Guid.NewGuid()));
            var backend = new WindowsTunnelBackend((_, _) => new FakeTunnelProcess(), _ => false, delay: NoDelay);
            var changes = backend.Prepare(lease);
            Assert(changes.Count == 1 && changes[0].Kind == NetworkResourceKind.TunnelInterface);
            Assert(changes[0].ResourceId == WindowsTunnelBackend.InterfaceName(lease.LeaseId));
            Assert(changes[0].ResourceId.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.Ordinal));
            Assert(changes[0].Before is null && changes[0].After.Contains("exclude=203.0.113.7"));
            // Имя интерфейса выводится из сессии, а не принимается снаружи.
            Assert(WindowsTunnelBackend.InterfaceName(lease.LeaseId) != WindowsTunnelBackend.InterfaceName(Guid.NewGuid()));
            // Запись из чужой сессии не распоряжается нашим интерфейсом.
            ExpectUserError(() => backend.Apply(Guid.NewGuid(), changes[0]));
            ExpectUserError(() => backend.Restore(Guid.NewGuid(), changes[0]));
        }));
        await check("Channel waits longer than the backend does for the adapter", () => Sync(() =>
        {
            // Найдено живым прогоном: клиент сдавался через 10 секунд, а помощник
            // законно ждал исчезновения адаптера дольше. Канал рвался, сессия уходила
            // в RecoveryRequired, хотя остановка на самом деле проходила успешно.
            Assert(HelperPipeClient.SessionTimeout > WindowsTunnelBackend.DefaultTimeout);
            // Запас нужен и на сам обмен по каналу, а не только на ожидание адаптера.
            Assert(HelperPipeClient.SessionTimeout - WindowsTunnelBackend.DefaultTimeout >= TimeSpan.FromSeconds(20));
            // Короткие команды остаются короткими: зависший помощник не держит окно минуту.
            Assert(HelperPipeClient.TimeoutFor(HelperOperation.Heartbeat) == HelperPipeClient.QuickTimeout);
            Assert(HelperPipeClient.TimeoutFor(HelperOperation.Acquire) == HelperPipeClient.SessionTimeout);
            Assert(HelperPipeClient.TimeoutFor(HelperOperation.Release) == HelperPipeClient.SessionTimeout);
            // Сердцебиение должно успевать несколько раз за срок чтения на стороне помощника.
            Assert(HelperPipeClient.QuickTimeout < TimeSpan.FromSeconds(15));
        }));
        await check("Full session starts the core, waits for the interface and removes it on release", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var name = WindowsTunnelBackend.InterfaceName(id);
            var up = false;
            var core = new FakeTunnelProcess();
            // Интерфейс живёт вместе с процессом: так ведёт себя wintun, это видно на стенде.
            var backend = new WindowsTunnelBackend((_, _) => { up = true; return core; },
                n => up && core.IsRunning && n == name, delay: NoDelay);
            var journal = new NetworkSessionJournal(Path.Combine(root, "tun-backend.json"));
            var session = new NetworkSession(journal, backend);

            var applied = session.Acquire(Request(id));
            Assert(applied.State == NetworkSessionState.Applied && up && core.Disposals == 0);
            // Пока сессия жива, вторая не начинается.
            ExpectUserError(() => session.Acquire(Request(Guid.NewGuid())));

            // Останавливаем так же, как это делает ядро: интерфейс исчезает вместе с процессом.
            var released = session.Release(id);
            Assert(released.State == NetworkSessionState.Released && core.Disposals == 1);
            Assert(released.Changes.All(c => c.State == NetworkChangeState.Restored));
        }));
        await check("Foreign interface with our name is a conflict, not something to tear down", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var backend = new WindowsTunnelBackend((_, _) => new FakeTunnelProcess(), _ => true, delay: NoDelay);
            var lease = NetworkHelperContracts.Validate(Request(id));
            var change = backend.Prepare(lease)[0];
            Assert(backend.Apply(id, change) == NetworkWriteResult.Conflict);
        }));
        await check("Recovery of a journal we do not own returns at once, without waiting", () => Sync(() =>
        {
            // После перезагрузки помощник видит чужой незавершённый журнал. Своего ядра у
            // него нет, поэтому ждать исчезновения адаптера бессмысленно — а на стенде это
            // ожидание стоило 25 секунд ДО открытия канала, и клиент не дожидался связи.
            var id = Guid.NewGuid();
            var waited = false;
            var backend = new WindowsTunnelBackend((_, _) => new FakeTunnelProcess(), _ => true,
                TimeSpan.FromSeconds(30), (_, _) => { waited = true; return Task.CompletedTask; });
            var lease = NetworkHelperContracts.Validate(Request(id));
            var change = backend.Prepare(lease)[0] with { State = NetworkChangeState.Applied };
            // Интерфейс намеренно «висит»: это не должно задерживать восстановление.
            Assert(backend.Restore(id, change) == NetworkWriteResult.Unchanged);
            Assert(!waited);
        }));
        await check("Channel allows for the helper recovering before it opens the pipe", () => Sync(() =>
        {
            // Помощник восстанавливает незавершённую сессию до создания канала.
            Assert(HelperPipeClient.ConnectTimeout > WindowsTunnelBackend.DefaultTimeout);
            Assert(HelperPipeClient.ConnectTimeout > HelperPipeClient.QuickTimeout);
        }));
        await check("Lingering adapter does not block the next session once the core is gone", () => Sync(() =>
        {
            // Найдено живым прогоном: Windows убирает wintun уже после смерти процесса, и
            // задержка доходит до минут. Решает процесс, а не адаптер: иначе безобидный
            // остаток навсегда запирал бы системный режим.
            var id = Guid.NewGuid();
            var core = new FakeTunnelProcess();
            var up = false;
            var backend = new WindowsTunnelBackend((_, _) => { up = true; return core; }, _ => up,
                TimeSpan.FromMilliseconds(30), NoDelay);
            var lease = NetworkHelperContracts.Validate(Request(id));
            var change = backend.Prepare(lease)[0];
            Assert(backend.Apply(id, change) == NetworkWriteResult.Changed);
            // Адаптер не исчезает, но ядро остановлено: сессия считается снятой.
            Assert(backend.Restore(id, change with { State = NetworkChangeState.Applied }) == NetworkWriteResult.Changed);
            Assert(core.Disposals == 1);
        }));
        await check("Core that refuses to stop is reported instead of being silently accepted", () => Sync(() =>
        {
            // Живое ядро с живым адаптером — настоящий отказ, а не остаток.
            var id = Guid.NewGuid();
            var stubborn = new StubbornTunnelProcess();
            var up = false;
            var backend = new WindowsTunnelBackend((_, _) => { up = true; return stubborn; }, _ => up,
                TimeSpan.FromMilliseconds(30), NoDelay);
            var lease = NetworkHelperContracts.Validate(Request(id));
            var change = backend.Prepare(lease)[0];
            Assert(backend.Apply(id, change) == NetworkWriteResult.Changed);
            // Ядро живо, адаптер на месте: это отказ остановки, а не остаток.
            var failed = false;
            try { backend.Restore(id, change with { State = NetworkChangeState.Applied }); } catch (IOException) { failed = true; }
            Assert(failed);
        }));
        await check("Helper processes a long operation without dropping the channel", () => Sync(() =>
        {
            // Найдено живым прогоном: один общий срок покрывал и ожидание запроса, и его
            // обработку. Release законно занимал дольше, срок истекал на записи ответа,
            // помощник завершался и рвал канал посреди успешной остановки.
            Assert(HelperPipeServer.DefaultSessionTimeout > WindowsTunnelBackend.DefaultTimeout);
            Assert(HelperPipeClient.SessionTimeout > HelperPipeServer.DefaultSessionTimeout);
        }));
        await check("Core that never raises an interface is stopped instead of left behind", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var core = new FakeTunnelProcess();
            var backend = new WindowsTunnelBackend((_, _) => core, _ => false, TimeSpan.FromMilliseconds(30), NoDelay);
            var lease = NetworkHelperContracts.Validate(Request(id));
            var change = backend.Prepare(lease)[0];
            ExpectUserError(() => backend.Apply(id, change));
            Assert(core.Disposals == 1);
        }));
        await check("Session recovery after an abandoned journal stops the core and releases", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var up = false;
            var core = new FakeTunnelProcess();
            var backend = new WindowsTunnelBackend((_, _) => { up = true; return core; }, _ => up, delay: NoDelay);
            var journal = new NetworkSessionJournal(Path.Combine(root, "tun-recovery.json"));
            var session = new NetworkSession(journal, backend);
            session.Acquire(Request(id));
            // Ядро убито вместе с владельцем: журнал остался в Applied, интерфейса уже нет.
            // Это ровно то, что показал стенд, и путь восстановления helper при запуске.
            up = false;
            var recovered = session.Recover();
            Assert(recovered!.State == NetworkSessionState.Released);
            Assert(recovered.Changes.All(c => c.State == NetworkChangeState.Restored));
        }));
        await check("A removed adapter's leftover record does not hold the release for the full timeout", () => Sync(() =>
        {
            // Найдено на стенде: после смены сервера остановка ждала 25 с. Ядро закрылось за
            // секунду, но .NET 10 продолжал перечислять удалённый адаптер в состоянии Down.
            var id = Guid.NewGuid();
            var up = false;
            var waits = 0;
            var recorded = false; // запись в списке появляется с адаптером и остаётся после него
            var backend = new WindowsTunnelBackend((_, _) => { up = recorded = true; return new FakeTunnelProcess(); },
                _ => recorded, TimeSpan.FromSeconds(25), (_, _) => { waits++; return Task.CompletedTask; }, interfaceUp: _ => up);
            var change = backend.Prepare(NetworkHelperContracts.Validate(Request(id)))[0];
            Assert(backend.Apply(id, change) == NetworkWriteResult.Changed);
            waits = 0;
            up = false; // ядро остановлено, адаптер не работает, но запись о нём осталась
            Assert(backend.Restore(id, change) == NetworkWriteResult.Changed);
            Assert(waits == 0);
        }));
        await check("Another VPN is recognised by the adapter that carries internet traffic", () => Sync(() =>
        {
            // Throne, WireGuard, sing-box — wintun (тип 53); OpenVPN — TAP; встроенный VPN — PPP.
            Assert(ForeignTunnel.IsForeign("throne-tun", 53, "sing-tun Tunnel"));
            Assert(ForeignTunnel.IsForeign("OpenVPN", 6, "TAP-Windows Adapter V9"));
            Assert(ForeignTunnel.IsForeign("Работа", (int)System.Net.NetworkInformation.NetworkInterfaceType.Ppp, "WAN Miniport (IKEv2)"));
            // Свой туннель и обычные сети не в счёт, в том числе Hyper-V с внешним коммутатором.
            Assert(!ForeignTunnel.IsForeign(WindowsTunnelBackend.InterfaceName(Guid.NewGuid()), 53, "sing-tun Tunnel"));
            Assert(!ForeignTunnel.IsForeign("Беспроводная сеть", 71, "Intel(R) Wi-Fi 6E AX210 160MHz"));
            Assert(!ForeignTunnel.IsForeign("vEthernet (External)", 6, "Hyper-V Virtual Ethernet Adapter"));
            Assert(ForeignTunnel.Explain("throne-tun").Contains("«throne-tun»", StringComparison.Ordinal));
        }));
        await Task.CompletedTask;
    }
}
